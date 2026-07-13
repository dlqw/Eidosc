using System.Globalization;
using System.Runtime.CompilerServices;
using Eidosc.Mir;
using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    /// <summary>
    /// Zero-allocation FNV-1a rolling hash accumulator for convergence detection.
    /// </summary>
    internal ref struct ConvergenceHash
    {
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private ulong _hash;

        public ConvergenceHash() => _hash = FnvOffsetBasis;

        public readonly ulong Value => _hash;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int value) { _hash ^= (uint)value; _hash *= FnvPrime; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(uint value) { _hash ^= value; _hash *= FnvPrime; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(long value) { _hash ^= (ulong)value; _hash *= FnvPrime; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(ulong value) { _hash ^= value; _hash *= FnvPrime; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(bool value) { _hash ^= value ? 1UL : 0UL; _hash *= FnvPrime; }

        /// <summary>
        /// Hash a string. Includes a delimiter byte to prevent
        /// "ab"+"c" from colliding with "a"+"bc".
        /// </summary>
        public void Add(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                foreach (var c in value)
                {
                    _hash ^= c;
                    _hash *= FnvPrime;
                }
            }
            _hash ^= 0xFF;
            _hash *= FnvPrime;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add<T>(T value) where T : struct, Enum
        {
            _hash ^= (ulong)(uint)value.GetHashCode();
            _hash *= FnvPrime;
        }

        // ── ordered collection helpers ──

        public void AddTypeIds(IEnumerable<TypeId> ids)
        {
            foreach (var id in ids) Add(id.Value);
            _hash ^= 0xFE; _hash *= FnvPrime;
        }

        public void AddSymbolIds(IEnumerable<SymbolId> ids)
        {
            foreach (var id in ids) Add(id.Value);
            _hash ^= 0xFE; _hash *= FnvPrime;
        }

        public void AddStrings(IEnumerable<string> values)
        {
            foreach (var v in values) Add(v);
            _hash ^= 0xFE; _hash *= FnvPrime;
        }

        public void AddInts(IEnumerable<int> values)
        {
            foreach (var v in values) Add(v);
            _hash ^= 0xFE; _hash *= FnvPrime;
        }
    }

    // ──────────────────────────────────────────────
    //  Entry point — replaces CreateMirModuleConvergenceFingerprint
    // ──────────────────────────────────────────────

    /// <summary>
    /// Compute a 64-bit rolling hash of the module for convergence detection.
    /// Order-independent for collections where the string fingerprint used OrderBy
    /// (XOR of per-element sub-hashes), sequential for ordered data.
    /// Skips intern tables (DynamicTypeKeys, TypeDescriptors) to match
    /// the original convergence fingerprint behaviour.
    /// </summary>
    internal static ulong ComputeConvergenceHash(MirModule module)
    {
        var h = new ConvergenceHash();

        h.Add("module");
        h.Add(module.Name);
        h.Add("path");
        foreach (var p in module.Path) h.Add(p);
        h.Add("");

        // ConstructorLayouts — unordered dict (was .OrderBy(key))
        h.Add("layouts");
        {
            ulong combined = 0;
            foreach (var (typeId, layouts) in module.ConstructorLayouts)
            {
                var eh = new ConvergenceHash();
                eh.Add(typeId);
                ulong innerHash = 0;
                foreach (var layout in layouts)
                {
                    var lh = new ConvergenceHash();
                    lh.Add(layout.TypeName);
                    lh.Add(layout.ConstructorName);
                    lh.Add(layout.TagValue);
                    lh.AddTypeIds(layout.FieldTypeIds);
                    innerHash ^= lh.Value;
                }
                eh.Add(innerHash);
                combined ^= eh.Value;
            }
            h.Add(combined);
        }

        // TraitImpls — unordered list (was .OrderBy(Id.Value))
        h.Add("trait-impls");
        {
            ulong combined = 0;
            foreach (var impl in module.TraitImpls)
            {
                var ih = new ConvergenceHash();
                HashTraitImpl(ref ih, impl);
                combined ^= ih.Value;
            }
            h.Add(combined);
        }

        // TraitInfos — unordered list (was .OrderBy(TraitId.Value))
        h.Add("trait-infos");
        {
            ulong combined = 0;
            foreach (var ti in module.TraitInfos)
            {
                var ih = new ConvergenceHash();
                ih.Add(ti.TraitId.Value);
                ih.Add(ti.TypeParameterCount);
                ih.AddSymbolIds(ti.TypeParameterIds);
                ih.Add(ti.SelfPosition);
                ih.Add(ti.HasMethodDispatchMetadata);
                combined ^= ih.Value;
            }
            h.Add(combined);
        }

        // TypeAliases — unordered list (was .OrderBy(AliasId.Value))
        h.Add("aliases");
        {
            ulong combined = 0;
            foreach (var alias in module.TypeAliases)
            {
                var ah = new ConvergenceHash();
                ah.Add(alias.AliasId.Value);
                ah.Add(alias.Name);
                ah.Add(alias.TypeId.Value);
                ah.Add(alias.AliasTarget.Value);
                ah.AddSymbolIds(alias.TypeParameterIds);
                combined ^= ah.Value;
            }
            h.Add(combined);
        }

        // SpecializationFailures — unordered list
        h.Add("failures");
        {
            ulong combined = 0;
            foreach (var f in module.SpecializationFailures)
            {
                var fh = new ConvergenceHash();
                fh.Add(f.Reason);
                fh.Add(f.TemplateKey);
                fh.Add(f.SignatureKey);
                fh.Add(f.PreviewName);
                combined ^= fh.Value;
            }
            h.Add(combined);
        }

        // Functions — ORDERED list (no sort in original)
        h.Add("functions");
        foreach (var func in module.Functions)
        {
            HashFunction(ref h, func);
        }

        return h.Value;
    }

    // ──────────────────────────────────────────────
    //  Function hashing
    // ──────────────────────────────────────────────

    private static void HashFunction(ref ConvergenceHash h, MirFunc function)
    {
        h.Add("func");
        h.Add(function.Name);
        h.Add(function.SymbolId.Value);
        HashFunctionId(ref h, function.FunctionId);
        h.Add(function.ReturnType.Value);
        h.Add(function.GenericParameterCount);
        h.AddTypeIds(function.GenericTypeParameterIds);
        h.Add(function.IsRuntimeWordAbi);
        h.Add(function.IsEntry);
        h.Add(function.IsExternal);
        h.Add(function.ExternalSymbolName ?? "");
        h.Add(function.ExternalLibrary ?? "");
        h.Add(function.EntryBlockId.Value);
        h.Add(function.TraitInvokeHelper);
        h.Add(function.TraitInvokeHelperTraitId.Value);

        // Locals — unordered (was .OrderBy(Id.Value))
        h.Add("locals");
        {
            ulong combined = 0;
            foreach (var local in function.Locals)
            {
                var lh = new ConvergenceHash();
                lh.Add(local.Id.Value);
                lh.Add(local.Name);
                lh.Add(local.TypeId.Value);
                lh.Add(local.IsMutable);
                lh.Add(local.IsParameter);
                lh.Add(local.BindingMode);
                combined ^= lh.Value;
            }
            h.Add(combined);
        }

        // Blocks — unordered (was .OrderBy(Id.Value))
        h.Add("blocks");
        {
            ulong combined = 0;
            foreach (var block in function.BasicBlocks)
            {
                var bh = new ConvergenceHash();
                bh.Add(block.Id.Value);
                bh.Add(block.IsEntry);
                bh.Add("instructions");
                foreach (var instr in block.Instructions)
                {
                    HashInstruction(ref bh, instr);
                }
                HashTerminator(ref bh, block.Terminator);
                combined ^= bh.Value;
            }
            h.Add(combined);
        }
    }

    // ──────────────────────────────────────────────
    //  Instruction hashing
    // ──────────────────────────────────────────────

    private static void HashInstruction(ref ConvergenceHash h, MirInstruction instruction)
    {
        switch (instruction)
        {
            case MirAssign assign:
                h.Add(1);
                HashPlace(ref h, assign.Target);
                HashOperand(ref h, assign.Source);
                break;
            case MirCall call:
                h.Add(2);
                HashPlace(ref h, call.Target);
                HashOperand(ref h, call.Function);
                HashOperands(ref h, call.Arguments);
                h.Add(call.IsTailCall);
                break;
            case MirBinOp binOp:
                h.Add(8);
                HashOperand(ref h, binOp.Target);
                h.Add(binOp.Operator);
                HashOperand(ref h, binOp.Left);
                HashOperand(ref h, binOp.Right);
                break;
            case MirUnaryOp unaryOp:
                h.Add(9);
                HashOperand(ref h, unaryOp.Target);
                h.Add(unaryOp.Operator);
                HashOperand(ref h, unaryOp.Operand);
                break;
            case MirLoad load:
                h.Add(10);
                HashPlace(ref h, load.Target);
                HashOperand(ref h, load.Source);
                h.Add(load.IsMutableBorrow);
                h.Add(load.CreatesBorrowAlias);
                break;
            case MirStore store:
                h.Add(11);
                HashPlace(ref h, store.Target);
                HashOperand(ref h, store.Value);
                break;
            case MirDrop drop:
                h.Add(12);
                HashOperand(ref h, drop.Value);
                break;
            case MirCopy copy:
                h.Add(13);
                HashPlace(ref h, copy.Target);
                HashPlace(ref h, copy.Source);
                break;
            case MirMove move:
                h.Add(14);
                HashPlace(ref h, move.Target);
                HashPlace(ref h, move.Source);
                break;
            case MirAlloc alloc:
                h.Add(15);
                HashPlace(ref h, alloc.Target);
                h.Add(alloc.TypeId.Value);
                break;
            default:
                h.Add(99);
                h.Add(instruction.GetType().FullName ?? instruction.GetType().Name);
                h.Add(instruction.ToString() ?? "");
                break;
        }
    }

    // ──────────────────────────────────────────────
    //  Terminator hashing
    // ──────────────────────────────────────────────

    private static void HashTerminator(ref ConvergenceHash h, MirTerminator? terminator)
    {
        switch (terminator)
        {
            case null:
                h.Add(0);
                break;
            case MirReturn ret:
                h.Add(1);
                HashOperand(ref h, ret.Value);
                break;
            case MirGoto goTo:
                h.Add(2);
                h.Add(goTo.Target.Value);
                break;
            case MirSwitch sw:
                h.Add(3);
                HashOperand(ref h, sw.Discriminant);
                foreach (var branch in sw.Branches)
                {
                    HashOperand(ref h, branch.Value);
                    h.Add(branch.Target.Value);
                    h.Add(branch.BoundVariable?.Value ?? 0);
                }
                h.Add(sw.DefaultTarget?.Value ?? 0);
                break;
            case MirUnreachable:
                h.Add(4);
                break;
            default:
                h.Add(99);
                h.Add(terminator.GetType().FullName ?? terminator.GetType().Name);
                h.Add(terminator.ToString() ?? "");
                break;
        }
    }

    // ──────────────────────────────────────────────
    //  Operand / Place hashing
    // ──────────────────────────────────────────────

    private static void HashOperands(ref ConvergenceHash h, IReadOnlyList<MirOperand> operands)
    {
        foreach (var operand in operands)
        {
            HashOperand(ref h, operand);
        }
    }

    private static void HashOperand(ref ConvergenceHash h, MirOperand? operand)
    {
        switch (operand)
        {
            case null:
                h.Add(0);
                break;
            case MirPoison poison:
                h.Add(1);
                h.Add(poison.TypeId.Value);
                h.Add(poison.Reason);
                break;
            case MirConstant constant:
                h.Add(2);
                h.Add(constant.TypeId.Value);
                HashConstantValue(ref h, constant.Value);
                break;
            case MirConstGenericValue constGeneric:
                h.Add(6);
                h.Add(constGeneric.TypeId.Value);
                h.Add(constGeneric.SymbolId.Value);
                h.Add(constGeneric.Name);
                h.Add(constGeneric.ParameterIndex);
                break;
            case MirFunctionRef functionRef:
                h.Add(3);
                h.Add(functionRef.TypeId.Value);
                h.Add(functionRef.SymbolId.Value);
                h.Add(functionRef.Name);
                h.Add(functionRef.SymbolKind);
                HashFunctionId(ref h, functionRef.FunctionId);
                h.Add(functionRef.SignatureTypeId.Value);
                h.AddTypeIds(functionRef.TypeArgumentIds);
                h.Add(functionRef.ValueArguments.Count);
                foreach (var argument in functionRef.ValueArguments)
                {
                    h.Add(argument.ParameterIndex);
                    h.Add(argument.CanonicalText);
                    h.Add(argument.CanonicalHash);
                    h.Add(argument.DisplayText);
                    h.Add(argument.TypeId.Value);
                    h.Add(argument.ReferencedParameterIndex);
                    h.Add(argument.ValueVariableIndex);
                }
                h.Add(functionRef.TraitOwnerId.Value);
                h.Add(functionRef.TraitSelfPosition);
                h.AddInts(functionRef.TraitSelfParameterIndices);
                h.Add(functionRef.TraitSelfInResult);
                h.Add(functionRef.TraitMethodRole);
                break;
            case MirPlace place:
                h.Add(4);
                HashPlace(ref h, place);
                break;
            case MirTemp temp:
                h.Add(5);
                h.Add(temp.TypeId.Value);
                h.Add(temp.Id.Value);
                break;
            default:
                h.Add(99);
                h.Add(operand.GetType().FullName ?? operand.GetType().Name);
                h.Add(operand.TypeId.Value);
                h.Add(operand.ToString() ?? "");
                break;
        }
    }

    private static void HashPlace(ref ConvergenceHash h, MirPlace? place)
    {
        if (place == null)
        {
            h.Add(0);
            return;
        }

        h.Add(1);
        h.Add(place.TypeId.Value);
        h.Add(place.Kind);
        h.Add(place.Local.Value);
        HashPlace(ref h, place.Base);
        h.Add(place.FieldName ?? "");
        HashOperand(ref h, place.Index);
        h.Add(place.IndexAccessKind);
    }

    // ──────────────────────────────────────────────
    //  Constant value hashing
    // ──────────────────────────────────────────────

    private static void HashConstantValue(ref ConvergenceHash h, MirConstantValue? value)
    {
        switch (value)
        {
            case null:
                h.Add(0);
                break;
            case MirConstantValue.IntValue intValue:
                h.Add(1);
                h.Add(intValue.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case MirConstantValue.FloatValue floatValue:
                h.Add(2);
                h.Add(floatValue.Value.ToString("R", CultureInfo.InvariantCulture));
                break;
            case MirConstantValue.StringValue stringValue:
                h.Add(3);
                h.Add(stringValue.Value);
                break;
            case MirConstantValue.RawStringValue rawStringValue:
                h.Add(4);
                h.Add(rawStringValue.Value);
                break;
            case MirConstantValue.CharValue charValue:
                h.Add(5);
                h.Add((int)charValue.Value);
                break;
            case MirConstantValue.BoolValue boolValue:
                h.Add(6);
                h.Add(boolValue.Value);
                break;
            case MirConstantValue.UnitValue:
                h.Add(7);
                break;
            default:
                h.Add(99);
                h.Add(value.GetType().FullName ?? value.GetType().Name);
                h.Add(value.ToString() ?? "");
                break;
        }
    }

    // ──────────────────────────────────────────────
    //  FunctionId hashing
    // ──────────────────────────────────────────────

    private static void HashFunctionId(ref ConvergenceHash h, FunctionId? functionId)
    {
        if (functionId == null)
        {
            h.Add("");
            return;
        }

        h.Add(functionId.StableIdentityKey);
        h.Add(string.IsNullOrWhiteSpace(functionId.StableIdentityKey)
            ? functionId.SymbolId.Value
            : SymbolId.None.Value);
        h.Add(functionId.Kind);
        h.Add(functionId.Module);
        h.Add(functionId.ModuleIdentityKey);
        h.Add(functionId.Name);
        h.Add(functionId.QualifiedName);
        h.Add(functionId.MangledName);
    }

    // ──────────────────────────────────────────────
    //  TraitImpl hashing
    // ──────────────────────────────────────────────

    private static void HashTraitImpl(ref ConvergenceHash h, ImplSymbol impl)
    {
        h.Add(impl.Id.Value);
        h.Add(impl.Trait.Value);
        h.Add(impl.ImplementingType.Value);
        HashImplTypeRefKey(ref h, impl.ImplementingTypeKey);
        HashImplShape(ref h, impl.ImplementingTypeShape);

        // Legacy implementing type text
        if (!impl.ImplementingTypeKey.IsEmpty || impl.ImplementingTypeShape != null)
        {
            h.Add("structured-implementing-type");
        }
        else
        {
            h.Add(impl.ImplementingTypeDisplay);
            h.Add(impl.CanonicalImplementingType);
        }

        h.AddSymbolIds(impl.Methods);
        HashImplTypeRefKeys(ref h, impl.TraitTypeArgKeys);
        HashImplTypeRefKeys(ref h, impl.CanonicalTraitTypeArgKeys);
        HashImplShapes(ref h, impl.TraitTypeArgShapes);

        // Legacy trait type arg text
        var hasStructured = impl.TraitTypeArgShapes.Count > 0;
        if (!hasStructured)
        {
            foreach (var key in impl.TraitTypeArgKeys)
            {
                if (!key.IsEmpty) { hasStructured = true; break; }
            }
        }
        if (!hasStructured)
        {
            foreach (var key in impl.CanonicalTraitTypeArgKeys)
            {
                if (!key.IsEmpty) { hasStructured = true; break; }
            }
        }
        if (hasStructured)
        {
            h.Add("structured-trait-type-args");
        }
        else
        {
            h.AddStrings(impl.TraitTypeArgs);
            h.AddStrings(impl.CanonicalTraitTypeArgs);
        }

        // TypeArguments — unordered dict (was .OrderBy(key.Value))
        {
            ulong combined = 0;
            foreach (var (from, to) in impl.TypeArguments)
            {
                var tah = new ConvergenceHash();
                tah.Add(from.Value);
                tah.Add(to.Value);
                combined ^= tah.Value;
            }
            h.Add(combined);
        }

        // ImplementingTypeRequirements — unordered list
        {
            ulong combined = 0;
            foreach (var req in impl.ImplementingTypeRequirements)
            {
                var rh = new ConvergenceHash();
                rh.Add(req.TypeArgIndex);
                rh.Add(req.Trait.Value);
                rh.Add(req.TraitName);
                HashImplTypeRefKeys(ref rh, req.TraitTypeArgKeys);
                if (!HasNonEmptyKey(req.TraitTypeArgKeys))
                {
                    rh.AddStrings(req.TraitTypeArgs);
                }
                combined ^= rh.Value;
            }
            h.Add(combined);
        }

        // TraitMethodImplementations — unordered dict (was .OrderBy(key.Value))
        {
            ulong combined = 0;
            foreach (var (traitMethod, implMethod) in impl.TraitMethodImplementations)
            {
                var mh = new ConvergenceHash();
                mh.Add(traitMethod.Value);
                mh.Add(implMethod.Value);
                combined ^= mh.Value;
            }
            h.Add(combined);
        }
    }

    private static bool HasNonEmptyKey(IEnumerable<ImplTypeRefKey> keys)
    {
        foreach (var key in keys)
        {
            if (!key.IsEmpty) return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────
    //  ImplTypeRefKey / ImplTypeShape hashing
    // ──────────────────────────────────────────────

    private static void HashImplTypeRefKeys(ref ConvergenceHash h, IEnumerable<ImplTypeRefKey> keys)
    {
        foreach (var key in keys)
        {
            HashImplTypeRefKey(ref h, key);
        }
        h.Add(0xFE);
    }

    private static void HashImplTypeRefKey(ref ConvergenceHash h, ImplTypeRefKey key)
    {
        h.Add("impl-key");
        if (key.IsEmpty)
        {
            h.Add("empty");
            return;
        }

        if (key.ValueArgument is { } valueArgument)
        {
            h.Add("value");
            h.Add(valueArgument.ParameterIndex);
            h.Add(valueArgument.CanonicalPayload);
            h.Add(valueArgument.TypeId.Value);
            h.Add(valueArgument.VariableIdentity);
            return;
        }

        if (key.TypeId.IsValid)
        {
            h.Add("type");
            h.Add(key.TypeId.Value);
        }
        else if (key.SymbolId.IsValid)
        {
            h.Add("symbol");
            h.Add(key.SymbolId.Value);
        }
        else
        {
            h.Add("text");
            h.Add(key.Text);
        }

        HashImplTypeRefKeys(ref h, key.TypeArguments);
    }

    private static void HashImplShapes(ref ConvergenceHash h, IEnumerable<ImplTypeShapeNode> shapes)
    {
        foreach (var shape in shapes)
        {
            HashImplShape(ref h, shape);
        }
        h.Add(0xFE);
    }

    private static void HashImplShape(ref ConvergenceHash h, ImplTypeShapeNode? shape)
    {
        switch (shape)
        {
            case null:
                h.Add(0);
                break;
            case ImplWildcardShapeNode:
                h.Add(1);
                break;
            case ImplVariableShapeNode variable:
                h.Add(2);
                h.Add(variable.Name);
                break;
            case ImplValueVariableShapeNode variable:
                h.Add(7);
                h.Add(variable.Name);
                h.Add(variable.TypeId.Value);
                break;
            case ImplConcreteValueShapeNode value:
                h.Add(8);
                h.Add(value.CanonicalPayload);
                h.Add(value.TypeId.Value);
                break;
            case ImplConstructorShapeNode constructor:
                h.Add(3);
                if (constructor.TypeId.IsValid)
                {
                    h.Add("type");
                    h.Add(constructor.TypeId.Value);
                }
                else if (constructor.SymbolId.IsValid)
                {
                    h.Add("symbol");
                    h.Add(constructor.SymbolId.Value);
                }
                else
                {
                    h.Add("text");
                    h.Add(constructor.Name);
                }
                HashImplShapes(ref h, constructor.Args);
                break;
            case ImplTupleShapeNode tuple:
                h.Add(4);
                HashImplShapes(ref h, tuple.Elements);
                break;
            case ImplArrowShapeNode arrow:
                h.Add(5);
                HashImplShape(ref h, arrow.ParamType);
                HashImplShape(ref h, arrow.ReturnType);
                break;
            case ImplEffectfulShapeNode effectful:
                h.Add(6);
                HashImplShape(ref h, effectful.InputType);
                h.AddStrings(effectful.EffectPaths);
                HashImplShape(ref h, effectful.OutputType);
                break;
            default:
                h.Add(99);
                h.Add(shape.GetType().FullName ?? shape.GetType().Name);
                h.Add(shape.ToString() ?? "");
                break;
        }
    }
}
