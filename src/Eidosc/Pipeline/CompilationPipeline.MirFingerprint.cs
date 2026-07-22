using Eidosc.Symbols;
using System.Globalization;
using System.Text;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    internal static string CreateMirModuleFingerprint(MirModule module)
    {
        return CreateMirModuleFingerprint(module, includeInternTables: true);
    }

    private static string CreateMirModuleFingerprint(MirModule module, bool includeInternTables)
    {
        var builder = new StringBuilder();

        Append(builder, "module");
        Append(builder, module.Name);
        Append(builder, "path");
        Append(builder, string.Join("/", module.Path));
        if (includeInternTables)
        {
            AppendMap(builder, "dynamic", module.DynamicTypeKeys.OrderBy(static entry => entry.Key));

            builder.Append("types[");
            foreach (var (typeId, descriptor) in module.TypeDescriptors.OrderBy(static entry => entry.Key))
            {
                Append(builder, typeId);
                builder.Append('=');
                AppendTypeDescriptor(builder, descriptor);
                builder.Append(';');
            }
            builder.Append(']');
        }

        builder.Append("layouts[");
        foreach (var (typeId, layouts) in module.ConstructorLayouts.OrderBy(static entry => entry.Key))
        {
            Append(builder, typeId);
            builder.Append(':');
            foreach (var layout in layouts
                         .OrderBy(static layout => layout.ConstructorName, StringComparer.Ordinal)
                         .ThenBy(static layout => layout.TagValue))
            {
                Append(builder, layout.TypeName);
                Append(builder, layout.ConstructorName);
                Append(builder, layout.TagValue);
                AppendTypeIds(builder, layout.FieldTypeIds);
            }
        }
        builder.Append(']');

        builder.Append("trait-impls[");
        foreach (var impl in module.TraitImpls.OrderBy(static impl => impl.Id.Value))
        {
            Append(builder, impl.Id.Value);
            Append(builder, impl.Trait.Value);
            Append(builder, impl.ImplementingType.Value);
            AppendImplTypeRefKey(builder, impl.ImplementingTypeKey);
            AppendImplShape(builder, impl.ImplementingTypeShape);
            AppendLegacyImplementingTypeText(builder, impl);
            AppendSymbolIds(builder, impl.Methods);
            // impl.Proofs removed during proof migration
            AppendImplTypeRefKeys(builder, impl.TraitTypeArgKeys);
            AppendImplTypeRefKeys(builder, impl.CanonicalTraitTypeArgKeys);
            AppendImplShapes(builder, impl.TraitTypeArgShapes);
            AppendLegacyTraitTypeArgText(builder, impl);
            builder.Append("type-args[");
            foreach (var (from, to) in impl.TypeArguments.OrderBy(static entry => entry.Key.Value))
            {
                Append(builder, from.Value);
                Append(builder, to.Value);
            }
            builder.Append(']');
            builder.Append("requirements[");
            foreach (var requirement in impl.ImplementingTypeRequirements
                         .OrderBy(static requirement => requirement.TypeArgIndex)
                         .ThenBy(static requirement => requirement.Trait.Value)
                         .ThenBy(static requirement => requirement.TraitName, StringComparer.Ordinal))
            {
                Append(builder, requirement.TypeArgIndex);
                Append(builder, requirement.Trait.Value);
                Append(builder, requirement.TraitName);
                AppendImplTypeRefKeys(builder, requirement.TraitTypeArgKeys);
                if (!requirement.TraitTypeArgKeys.Any(static key => !key.IsEmpty))
                {
                    AppendStrings(builder, requirement.TraitTypeArgs);
                }
            }
            builder.Append(']');
            builder.Append("method-map[");
            foreach (var (traitMethod, implMethod) in impl.TraitMethodImplementations.OrderBy(static entry => entry.Key.Value))
            {
                Append(builder, traitMethod.Value);
                Append(builder, implMethod.Value);
            }
            builder.Append(']');
        }
        builder.Append(']');

        builder.Append("trait-infos[");
        foreach (var traitInfo in module.TraitInfos.OrderBy(static trait => trait.TraitId.Value))
        {
            Append(builder, traitInfo.TraitId.Value);
            Append(builder, traitInfo.TypeParameterCount);
            AppendSymbolIds(builder, traitInfo.TypeParameterIds);
            Append(builder, traitInfo.SelfPosition);
            Append(builder, traitInfo.HasMethodDispatchMetadata);
        }
        builder.Append(']');

        builder.Append("aliases[");
        foreach (var alias in module.TypeAliases.OrderBy(static alias => alias.AliasId.Value))
        {
            Append(builder, alias.AliasId.Value);
            Append(builder, alias.Name);
            Append(builder, alias.TypeId.Value);
            Append(builder, alias.AliasTarget.Value);
            AppendSymbolIds(builder, alias.TypeParameterIds);
        }
        builder.Append(']');

        builder.Append("failures[");
        foreach (var failure in module.SpecializationFailures
                     .OrderBy(static failure => failure.TemplateKey, StringComparer.Ordinal)
                     .ThenBy(static failure => failure.SignatureKey, StringComparer.Ordinal)
                     .ThenBy(static failure => failure.Reason, StringComparer.Ordinal))
        {
            Append(builder, failure.Reason);
            Append(builder, failure.TemplateKey);
            Append(builder, failure.SignatureKey);
            Append(builder, failure.PreviewName);
        }
        builder.Append(']');

        builder.Append("functions[");
        foreach (var function in module.Functions)
        {
            AppendFunction(builder, function);
        }
        builder.Append(']');

        return builder.ToString();
    }

    private static void AppendFunction(StringBuilder builder, MirFunc function)
    {
        Append(builder, "func");
        Append(builder, function.Name);
        Append(builder, string.IsNullOrWhiteSpace(function.FunctionId.StableIdentityKey)
            ? function.SymbolId.Value
            : SymbolId.None.Value);
        AppendFunctionId(builder, function.FunctionId);
        Append(builder, function.ReturnType.Value);
        Append(builder, function.GenericParameterCount);
        builder.Append("generic-parameters[");
        foreach (var parameter in function.GenericParameters)
        {
            Append(builder, parameter.ParameterIndex);
            Append(builder, parameter.SymbolId.Value);
            Append(builder, parameter.Name);
            Append(builder, parameter.ParameterKind);
            Append(builder, parameter.TypeId.Value);
        }
        builder.Append(']');
        AppendTypeIds(builder, function.GenericTypeParameterIds);
        Append(builder, function.IsRuntimeWordAbi);
        Append(builder, function.IsEntry);
        Append(builder, function.IsExternal);
        Append(builder, function.ExternalSymbolName ?? "");
        Append(builder, function.ExternalLibrary ?? "");
        Append(builder, function.EntryBlockId.Value);
        Append(builder, function.TraitInvokeHelper);
        Append(builder, function.TraitInvokeHelperTraitId.Value);

        builder.Append("locals[");
        foreach (var local in function.Locals.OrderBy(static local => local.Id.Value))
        {
            Append(builder, local.Id.Value);
            Append(builder, local.Name);
            Append(builder, local.TypeId.Value);
            Append(builder, local.IsMutable);
            Append(builder, local.IsParameter);
            Append(builder, local.BindingMode);
        }
        builder.Append(']');

        builder.Append("blocks[");
        foreach (var block in function.BasicBlocks.OrderBy(static block => block.Id.Value))
        {
            Append(builder, block.Id.Value);
            Append(builder, block.IsEntry);
            builder.Append("instructions[");
            foreach (var instruction in block.Instructions)
            {
                AppendInstruction(builder, instruction);
            }
            builder.Append(']');
            AppendTerminator(builder, block.Terminator);
        }
        builder.Append(']');
    }

    private static void AppendInstruction(StringBuilder builder, MirInstruction instruction)
    {
        switch (instruction)
        {
            case MirAssign assign:
                Append(builder, nameof(MirAssign));
                AppendPlace(builder, assign.Target);
                AppendOperand(builder, assign.Source);
                break;
            case MirCaseInject injection:
                Append(builder, nameof(MirCaseInject));
                AppendOperand(builder, injection.Target);
                AppendOperand(builder, injection.Operand);
                Append(builder, injection.SourceTypeId.Value);
                Append(builder, injection.TargetTypeId.Value);
                break;
            case MirCall call:
                Append(builder, nameof(MirCall));
                AppendPlace(builder, call.Target);
                AppendOperand(builder, call.Function);
                AppendOperands(builder, call.Arguments);
                Append(builder, call.IsTailCall);
                break;
            case MirBinOp binOp:
                Append(builder, nameof(MirBinOp));
                AppendOperand(builder, binOp.Target);
                Append(builder, binOp.Operator);
                AppendOperand(builder, binOp.Left);
                AppendOperand(builder, binOp.Right);
                break;
            case MirUnaryOp unaryOp:
                Append(builder, nameof(MirUnaryOp));
                AppendOperand(builder, unaryOp.Target);
                Append(builder, unaryOp.Operator);
                AppendOperand(builder, unaryOp.Operand);
                break;
            case MirLoad load:
                Append(builder, nameof(MirLoad));
                AppendPlace(builder, load.Target);
                AppendOperand(builder, load.Source);
                Append(builder, load.IsMutableBorrow);
                Append(builder, load.CreatesBorrowAlias);
                break;
            case MirStore store:
                Append(builder, nameof(MirStore));
                AppendPlace(builder, store.Target);
                AppendOperand(builder, store.Value);
                break;
            case MirDrop drop:
                Append(builder, nameof(MirDrop));
                AppendOperand(builder, drop.Value);
                break;
            case MirCopy copy:
                Append(builder, nameof(MirCopy));
                AppendPlace(builder, copy.Target);
                AppendPlace(builder, copy.Source);
                break;
            case MirMove move:
                Append(builder, nameof(MirMove));
                AppendPlace(builder, move.Target);
                AppendPlace(builder, move.Source);
                break;
            case MirAlloc alloc:
                Append(builder, nameof(MirAlloc));
                AppendPlace(builder, alloc.Target);
                Append(builder, alloc.TypeId.Value);
                break;
            default:
                Append(builder, instruction.GetType().FullName ?? instruction.GetType().Name);
                Append(builder, instruction.ToString() ?? "");
                break;
        }
    }

    private static void AppendTerminator(StringBuilder builder, MirTerminator? terminator)
    {
        switch (terminator)
        {
            case null:
                Append(builder, "no-terminator");
                break;
            case MirReturn ret:
                Append(builder, nameof(MirReturn));
                AppendOperand(builder, ret.Value);
                break;
            case MirGoto goTo:
                Append(builder, nameof(MirGoto));
                Append(builder, goTo.Target.Value);
                break;
            case MirSwitch sw:
                Append(builder, nameof(MirSwitch));
                AppendOperand(builder, sw.Discriminant);
                foreach (var branch in sw.Branches)
                {
                    AppendOperand(builder, branch.Value);
                    Append(builder, branch.Target.Value);
                    Append(builder, branch.BoundVariable?.Value ?? 0);
                }
                Append(builder, sw.DefaultTarget?.Value ?? 0);
                break;
            case MirUnreachable:
                Append(builder, nameof(MirUnreachable));
                break;
            default:
                Append(builder, terminator.GetType().FullName ?? terminator.GetType().Name);
                Append(builder, terminator.ToString() ?? "");
                break;
        }
    }

    private static void AppendOperands(StringBuilder builder, IReadOnlyList<MirOperand> operands)
    {
        builder.Append('[');
        foreach (var operand in operands)
        {
            AppendOperand(builder, operand);
        }
        builder.Append(']');
    }

    private static void AppendOperand(StringBuilder builder, MirOperand? operand)
    {
        switch (operand)
        {
            case null:
                Append(builder, "null");
                break;
            case MirPoison poison:
                Append(builder, nameof(MirPoison));
                Append(builder, poison.TypeId.Value);
                Append(builder, poison.Reason);
                break;
            case MirConstant constant:
                Append(builder, nameof(MirConstant));
                Append(builder, constant.TypeId.Value);
                AppendConstantValue(builder, constant.Value);
                break;
            case MirConstGenericValue constGeneric:
                Append(builder, nameof(MirConstGenericValue));
                Append(builder, constGeneric.TypeId.Value);
                Append(builder, constGeneric.SymbolId.Value);
                Append(builder, constGeneric.Name);
                Append(builder, constGeneric.ParameterIndex);
                break;
            case MirFunctionRef functionRef:
                Append(builder, nameof(MirFunctionRef));
                Append(builder, functionRef.TypeId.Value);
                Append(builder, string.IsNullOrWhiteSpace(functionRef.FunctionId.StableIdentityKey)
                    ? functionRef.SymbolId.Value
                    : SymbolId.None.Value);
                Append(builder, functionRef.Name);
                Append(builder, functionRef.SymbolKind);
                AppendFunctionId(builder, functionRef.FunctionId);
                Append(builder, functionRef.SignatureTypeId.Value);
                AppendTypeIds(builder, functionRef.TypeArgumentIds);
                AppendValueArguments(builder, functionRef.ValueArguments);
                Append(builder, functionRef.TraitOwnerId.Value);
                Append(builder, functionRef.TraitSelfPosition);
                AppendInts(builder, functionRef.TraitSelfParameterIndices);
                Append(builder, functionRef.TraitSelfInResult);
                Append(builder, functionRef.TraitMethodRole);
                break;
            case MirPlace place:
                AppendPlace(builder, place);
                break;
            case MirTemp temp:
                Append(builder, nameof(MirTemp));
                Append(builder, temp.TypeId.Value);
                Append(builder, temp.Id.Value);
                break;
            default:
                Append(builder, operand.GetType().FullName ?? operand.GetType().Name);
                Append(builder, operand.TypeId.Value);
                Append(builder, operand.ToString() ?? "");
                break;
        }
    }

    private static void AppendPlace(StringBuilder builder, MirPlace? place)
    {
        if (place == null)
        {
            Append(builder, "null-place");
            return;
        }

        Append(builder, nameof(MirPlace));
        Append(builder, place.TypeId.Value);
        Append(builder, place.Kind);
        Append(builder, place.Local.Value);
        AppendPlace(builder, place.Base);
        Append(builder, place.FieldName ?? "");
        AppendOperand(builder, place.Index);
        Append(builder, place.IndexAccessKind);
    }

    private static void AppendConstantValue(StringBuilder builder, MirConstantValue? value)
    {
        switch (value)
        {
            case null:
                Append(builder, "null-constant");
                break;
            case MirConstantValue.IntValue intValue:
                Append(builder, nameof(MirConstantValue.IntValue));
                Append(builder, intValue.Value);
                break;
            case MirConstantValue.FloatValue floatValue:
                Append(builder, nameof(MirConstantValue.FloatValue));
                Append(builder, floatValue.Value.ToString("R", CultureInfo.InvariantCulture));
                break;
            case MirConstantValue.StringValue stringValue:
                Append(builder, nameof(MirConstantValue.StringValue));
                Append(builder, stringValue.Value);
                break;
            case MirConstantValue.RawStringValue rawStringValue:
                Append(builder, nameof(MirConstantValue.RawStringValue));
                Append(builder, rawStringValue.Value);
                break;
            case MirConstantValue.CharValue charValue:
                Append(builder, nameof(MirConstantValue.CharValue));
                Append(builder, (int)charValue.Value);
                break;
            case MirConstantValue.BoolValue boolValue:
                Append(builder, nameof(MirConstantValue.BoolValue));
                Append(builder, boolValue.Value);
                break;
            case MirConstantValue.UnitValue:
                Append(builder, nameof(MirConstantValue.UnitValue));
                break;
            default:
                Append(builder, value.GetType().FullName ?? value.GetType().Name);
                Append(builder, value.ToString() ?? "");
                break;
        }
    }

    private static void AppendTypeDescriptor(StringBuilder builder, TypeDescriptor descriptor)
    {
        switch (descriptor)
        {
            case TypeDescriptor.Builtin builtin:
                Append(builder, nameof(TypeDescriptor.Builtin));
                Append(builder, builtin.TypeIdValue);
                break;
            case TypeDescriptor.Function function:
                Append(builder, nameof(TypeDescriptor.Function));
                AppendTypeIds(builder, function.ParamTypes);
                Append(builder, function.ReturnType.Value);
                Append(builder, function.Effects ?? "");
                break;
            case TypeDescriptor.Tuple tuple:
                Append(builder, nameof(TypeDescriptor.Tuple));
                AppendTypeIds(builder, tuple.FieldTypes);
                break;
            case TypeDescriptor.TyCon tyCon:
                Append(builder, nameof(TypeDescriptor.TyCon));
                Append(builder, tyCon.ConstructorDescriptor);
                AppendTypeIds(builder, tyCon.TypeArgs);
                AppendValueArguments(builder, tyCon.ValueArgs);
                AppendEffectArguments(builder, tyCon.EffectArgs);
                break;
            case TypeDescriptor.Ref reference:
                Append(builder, nameof(TypeDescriptor.Ref));
                Append(builder, reference.Inner.Value);
                break;
            case TypeDescriptor.MutRef reference:
                Append(builder, nameof(TypeDescriptor.MutRef));
                Append(builder, reference.Inner.Value);
                break;
            case TypeDescriptor.TypeVar typeVar:
                Append(builder, nameof(TypeDescriptor.TypeVar));
                Append(builder, typeVar.Index);
                break;
            default:
                Append(builder, descriptor.GetType().FullName ?? descriptor.GetType().Name);
                Append(builder, descriptor.ToString() ?? "");
                break;
        }
    }

    private static void AppendFunctionId(StringBuilder builder, FunctionId? functionId)
    {
        if (functionId == null)
        {
            Append(builder, "");
            return;
        }

        Append(builder, functionId.StableIdentityKey);
        Append(builder, string.IsNullOrWhiteSpace(functionId.StableIdentityKey)
            ? functionId.SymbolId.Value
            : SymbolId.None.Value);
        Append(builder, functionId.Kind);
        Append(builder, functionId.Module);
        Append(builder, functionId.ModuleIdentityKey);
        Append(builder, functionId.Name);
        Append(builder, functionId.QualifiedName);
        Append(builder, functionId.MangledName);
    }

    private static void AppendTypeIds(StringBuilder builder, IEnumerable<TypeId> typeIds)
    {
        builder.Append('[');
        foreach (var typeId in typeIds)
        {
            Append(builder, typeId.Value);
        }
        builder.Append(']');
    }

    private static void AppendValueArguments(
        StringBuilder builder,
        IEnumerable<GenericValueArgumentDescriptor> arguments)
    {
        builder.Append('[');
        foreach (var argument in arguments)
        {
            Append(builder, argument.ParameterIndex);
            Append(builder, argument.CanonicalText);
            Append(builder, argument.CanonicalHash);
            Append(builder, argument.DisplayText);
            Append(builder, argument.TypeId.Value);
            Append(builder, argument.ReferencedParameterIndex);
            Append(builder, argument.ValueVariableIndex);
        }
        builder.Append(']');
    }

    private static void AppendEffectArguments(
        StringBuilder builder,
        IEnumerable<GenericEffectArgumentDescriptor> arguments)
    {
        builder.Append('[');
        foreach (var argument in arguments)
        {
            Append(builder, argument.ParameterIndex);
            Append(builder, argument.CanonicalText);
            Append(builder, argument.TypeId.Value);
        }
        builder.Append(']');
    }

    private static void AppendSymbolIds(StringBuilder builder, IEnumerable<SymbolId> symbolIds)
    {
        builder.Append('[');
        foreach (var symbolId in symbolIds)
        {
            Append(builder, symbolId.Value);
        }
        builder.Append(']');
    }

    private static void AppendInts(StringBuilder builder, IEnumerable<int> values)
    {
        builder.Append('[');
        foreach (var value in values)
        {
            Append(builder, value);
        }
        builder.Append(']');
    }

    private static void AppendStrings(StringBuilder builder, IEnumerable<string> values)
    {
        builder.Append('[');
        foreach (var value in values)
        {
            Append(builder, value);
        }
        builder.Append(']');
    }

    private static void AppendLegacyImplementingTypeText(StringBuilder builder, ImplSymbol impl)
    {
        if (!impl.ImplementingTypeKey.IsEmpty || impl.ImplementingTypeShape != null)
        {
            Append(builder, "structured-implementing-type");
            return;
        }

        Append(builder, impl.ImplementingTypeDisplay);
        Append(builder, impl.CanonicalImplementingType);
    }

    private static void AppendLegacyTraitTypeArgText(StringBuilder builder, ImplSymbol impl)
    {
        if (impl.TraitTypeArgShapes.Count > 0 ||
            impl.TraitTypeArgKeys.Any(static key => !key.IsEmpty) ||
            impl.CanonicalTraitTypeArgKeys.Any(static key => !key.IsEmpty))
        {
            Append(builder, "structured-trait-type-args");
            return;
        }

        AppendStrings(builder, impl.TraitTypeArgs);
        AppendStrings(builder, impl.CanonicalTraitTypeArgs);
    }

    private static void AppendImplTypeRefKeys(StringBuilder builder, IEnumerable<ImplTypeRefKey> keys)
    {
        builder.Append('[');
        foreach (var key in keys)
        {
            AppendImplTypeRefKey(builder, key);
        }
        builder.Append(']');
    }

    private static void AppendImplTypeRefKey(StringBuilder builder, ImplTypeRefKey key)
    {
        Append(builder, "impl-key");
        if (key.IsEmpty)
        {
            Append(builder, "empty");
            return;
        }

        if (key.ValueArgument is { } valueArgument)
        {
            Append(builder, "value");
            Append(builder, valueArgument.ParameterIndex);
            Append(builder, valueArgument.CanonicalPayload);
            Append(builder, valueArgument.TypeId.Value);
            Append(builder, valueArgument.VariableIdentity);
            return;
        }

        if (key.TypeId.IsValid)
        {
            Append(builder, "type");
            Append(builder, key.TypeId.Value);
        }
        else if (key.SymbolId.IsValid)
        {
            Append(builder, "symbol");
            Append(builder, key.SymbolId.Value);
        }
        else
        {
            Append(builder, "text");
            Append(builder, key.Text);
        }

        AppendImplTypeRefKeys(builder, key.TypeArguments);
    }

    private static void AppendImplShapes(StringBuilder builder, IEnumerable<ImplTypeShapeNode> shapes)
    {
        builder.Append('[');
        foreach (var shape in shapes)
        {
            AppendImplShape(builder, shape);
        }
        builder.Append(']');
    }

    private static void AppendImplShape(StringBuilder builder, ImplTypeShapeNode? shape)
    {
        switch (shape)
        {
            case null:
                Append(builder, "null-shape");
                break;
            case ImplWildcardShapeNode:
                Append(builder, nameof(ImplWildcardShapeNode));
                break;
            case ImplVariableShapeNode variable:
                Append(builder, nameof(ImplVariableShapeNode));
                Append(builder, variable.Name);
                break;
            case ImplValueVariableShapeNode variable:
                Append(builder, nameof(ImplValueVariableShapeNode));
                Append(builder, variable.Name);
                Append(builder, variable.TypeId.Value);
                break;
            case ImplConcreteValueShapeNode value:
                Append(builder, nameof(ImplConcreteValueShapeNode));
                Append(builder, value.CanonicalPayload);
                Append(builder, value.TypeId.Value);
                break;
            case ImplConstructorShapeNode constructor:
                Append(builder, nameof(ImplConstructorShapeNode));
                if (constructor.TypeId.IsValid)
                {
                    Append(builder, "type");
                    Append(builder, constructor.TypeId.Value);
                }
                else if (constructor.SymbolId.IsValid)
                {
                    Append(builder, "symbol");
                    Append(builder, constructor.SymbolId.Value);
                }
                else
                {
                    Append(builder, "text");
                    Append(builder, constructor.Name);
                }

                AppendImplShapes(builder, constructor.Args);
                break;
            case ImplTupleShapeNode tuple:
                Append(builder, nameof(ImplTupleShapeNode));
                AppendImplShapes(builder, tuple.Elements);
                break;
            case ImplArrowShapeNode arrow:
                Append(builder, nameof(ImplArrowShapeNode));
                AppendImplShape(builder, arrow.ParamType);
                AppendImplShape(builder, arrow.ReturnType);
                break;
            case ImplEffectfulShapeNode effectful:
                Append(builder, nameof(ImplEffectfulShapeNode));
                AppendImplShape(builder, effectful.InputType);
                AppendStrings(builder, effectful.EffectPaths);
                AppendImplShape(builder, effectful.OutputType);
                break;
            default:
                Append(builder, shape.GetType().FullName ?? shape.GetType().Name);
                Append(builder, shape.ToString() ?? "");
                break;
        }
    }

    private static void AppendMap(StringBuilder builder, string label, IEnumerable<KeyValuePair<int, string>> entries)
    {
        builder.Append(label).Append('[');
        foreach (var (key, value) in entries)
        {
            Append(builder, key);
            Append(builder, value);
        }
        builder.Append(']');
    }

    private static void Append<T>(StringBuilder builder, T value)
    {
        var text = value switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(text);
        builder.Append('|');
    }
}
