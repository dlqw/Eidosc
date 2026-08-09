using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static void AddFunctionalConstraint(TypeParam parameter, string traitName, string constructorName, SourceSpan span)
    {
        if (parameter.TraitConstraints.Any(constraint => string.Equals(constraint.TraitName, traitName, StringComparison.Ordinal)))
            return;

        var trait = new TraitRef();
        trait.SetTraitName(traitName);
        trait.SetSpan(span);
        trait.TypeArgs.Add(CreateTypePath(constructorName));
        parameter.TraitConstraints.Add(trait);
    }

    private enum FunctionalFieldShape
    {
        None,
        Direct,
        Nested,
        Unsupported
    }

    private readonly record struct FunctionalFieldAnalysis(FunctionalFieldShape Shape, TypeNode? NestedType, string Reason);

    private List<PatternBranch> GenerateFunctorBranches(AdtDef adt, int mappedParameterIndex, SourceSpan span)
    {
        if (!TryAnalyzeFunctionalFields(adt, mappedParameterIndex, "Functor", span))
            return [];

        var branches = new List<PatternBranch>();
        foreach (var ctor in adt.Constructors)
        {
            var vars = MakeVarPatterns(GetConstructorRuntimeFieldCount(ctor), "value", span);
            var fields = EnumerateConstructorFields(ctor).ToList();
            var mapped = fields.Select((field, index) =>
                BuildFunctorValue(field.Type!, MakeIdent(vars[index].Name, span), MakeIdent("f", span), adt.TypeParams[mappedParameterIndex].Name, span)).ToList();
            var branch = new PatternBranch();
            branch.SetParameterPatterns([MakeCtorPattern(ctor, vars, span), MakeNamedVarPattern("f", span)]);
            branch.SetExpression(MakeCtorExpr(ctor, mapped, span));
            branches.Add(branch);
        }

        return branches;
    }

    private List<PatternBranch> GenerateFoldableBranches(AdtDef adt, int mappedParameterIndex, SourceSpan span)
    {
        if (!TryAnalyzeFunctionalFields(adt, mappedParameterIndex, "Foldable", span))
            return [];

        var branches = new List<PatternBranch>();
        foreach (var ctor in adt.Constructors)
        {
            var vars = MakeVarPatterns(GetConstructorRuntimeFieldCount(ctor), "value", span);
            var fields = EnumerateConstructorFields(ctor).ToList();
            var initial = MakeIdent("initial", span);
            var reducer = MakeIdent("f", span);
            EidosAstNode expression = initial;
            for (var index = 0; index < fields.Count; index++)
            {
                expression = BuildFoldableStep(fields[index].Type!, expression, MakeIdent(vars[index].Name, span), reducer,
                    adt.TypeParams[mappedParameterIndex].Name, span);
            }

            var branch = new PatternBranch();
            branch.SetParameterPatterns([MakeCtorPattern(ctor, vars, span), MakeNamedVarPattern("initial", span), MakeNamedVarPattern("f", span)]);
            branch.SetExpression(expression);
            branches.Add(branch);
        }

        return branches;
    }

    private List<PatternBranch> GenerateTraversableBranches(AdtDef adt, int mappedParameterIndex, SourceSpan span)
    {
        if (!TryAnalyzeFunctionalFields(adt, mappedParameterIndex, "Traversable", span))
            return [];

        var branches = new List<PatternBranch>();
        foreach (var ctor in adt.Constructors)
        {
            var vars = MakeVarPatterns(GetConstructorRuntimeFieldCount(ctor), "value", span);
            var fields = EnumerateConstructorFields(ctor).ToList();
            EidosAstNode expression;
            if (fields.Count == 0)
            {
                expression = MakePreludeInternalCall("Traversable", "lift_pure", [MakeCtorExpr(ctor, [], span)], span);
            }
            else
            {
                var helperName = CreateTraversableConstructorHelperName(adt, ctor, adt.Constructors.IndexOf(ctor));
                var effects = fields.Select((field, index) =>
                    BuildTraversableValue(field.Type!, MakeIdent(vars[index].Name, span), MakeIdent("f", span),
                        adt.TypeParams[mappedParameterIndex].Name, span)).ToList();
                var constructorFunction = effects.Count >= 2
                    ? MakeConstructorLambda(ctor, fields.Count, span)
                    : MakeIdent(helperName, span);
                if (effects.Count == 1)
                {
                    expression = MakePreludeInternalCall(
                        "Traversable",
                        "map_applicative",
                        [constructorFunction, effects[0]],
                        span);
                }
                else
                {
                    if (effects.Count == 2)
                    {
                        expression = MakePreludeInternalCall(
                            "Traversable",
                            "map2_saturated",
                            [MakeTupleExpr([constructorFunction, effects[0], effects[1]], span)],
                            span);
                    }
                    else
                    {
                        var applyFunction = MakeApplyFunctionLambda(span);
                        expression = MakePreludeInternalCall(
                            "Traversable",
                            "map_applicative",
                            [constructorFunction, effects[0]],
                            span);
                        for (var index = 1; index < effects.Count; index++)
                        {
                            expression = MakePreludeInternalCall(
                                "Traversable",
                                "map2_saturated",
                                [MakeTupleExpr([applyFunction, expression, effects[index]], span)],
                                span);
                        }
                    }
                }
            }

            var branch = new PatternBranch();
            branch.SetParameterPatterns([MakeCtorPattern(ctor, vars, span), MakeNamedVarPattern("f", span)]);
            branch.SetExpression(expression);
            branches.Add(branch);
        }

        return branches;
    }

    private bool TryAnalyzeFunctionalFields(AdtDef adt, int mappedParameterIndex, string traitName, SourceSpan span)
    {
        var mappedName = adt.TypeParams[mappedParameterIndex].Name;
        var valid = true;
        foreach (var ctor in adt.Constructors)
        {
            foreach (var (field, index) in EnumerateConstructorFields(ctor).Select((field, index) => (field, index)))
            {
                var analysis = AnalyzeFunctionalField(field.Type!, mappedName);
                if (analysis.Shape != FunctionalFieldShape.Unsupported)
                    continue;

                var fieldName = field.Name.Length == 0 ? $"#{index + 1}" : field.Name;
                AddError(field.Type!.Span, DiagnosticMessages.DeriveFunctionalFieldUnsupported(traitName, adt.Name, ctor.Name, fieldName, analysis.Reason));
                valid = false;
            }
        }

        return valid;
    }

    private static FunctionalFieldAnalysis AnalyzeFunctionalField(TypeNode type, string mappedName)
    {
        switch (type)
        {
            case TypePath path when IsMappedType(path, mappedName):
                return new(FunctionalFieldShape.Direct, null, "");
            case TypePath path:
            {
                var contains = path.TypeArgs.Select(arg => TypeUsesTypeParameter(arg, mappedName)).ToArray();
                if (contains.Take(Math.Max(0, contains.Length - 1)).Any(value => value))
                    return new(FunctionalFieldShape.Unsupported, null, "the mapped parameter occurs in a non-final type argument");
                if (contains.Length > 0 && contains[^1])
                    return new(FunctionalFieldShape.Nested, path.TypeArgs[^1], "");
                return new(FunctionalFieldShape.None, null, "");
            }
            case ArrowType when TypeUsesTypeParameter(type, mappedName):
                return new(FunctionalFieldShape.Unsupported, null, "function and contravariant field shapes are not supported");
            case TupleType when TypeUsesTypeParameter(type, mappedName):
                return new(FunctionalFieldShape.Unsupported, null, "tuple field shapes are not supported");
            case EffectfulType when TypeUsesTypeParameter(type, mappedName):
                return new(FunctionalFieldShape.Unsupported, null, "effectful field shapes are not supported");
            default:
                return new(FunctionalFieldShape.None, null, "");
        }
    }

    private static bool IsMappedType(TypePath path, string mappedName) =>
        path.PackageAlias == null && path.ModulePath.Count == 0 && path.TypeArgs.Count == 0 &&
        path.GenericArguments.Count == 0 && string.Equals(path.TypeName, mappedName, StringComparison.Ordinal);

    private EidosAstNode BuildFunctorValue(TypeNode type, EidosAstNode value, EidosAstNode mapper, string mappedName, SourceSpan span)
    {
        var analysis = AnalyzeFunctionalField(type, mappedName);
        return analysis.Shape switch
        {
            FunctionalFieldShape.Direct => MakeCurriedPathCall("f", [value], span, mapper),
            FunctionalFieldShape.Nested => MakeCurriedPathCall("Functor.fmap", [value,
                MakeLambda("inner", BuildFunctorValue(analysis.NestedType!, MakeIdent("inner", span), mapper, mappedName, span), span)], span),
            _ => value
        };
    }

    private EidosAstNode BuildFoldableStep(TypeNode type, EidosAstNode accumulator, EidosAstNode value, EidosAstNode reducer, string mappedName, SourceSpan span)
    {
        var analysis = AnalyzeFunctionalField(type, mappedName);
        return analysis.Shape switch
        {
            FunctionalFieldShape.Direct => MakeCurriedCall(reducer, [accumulator, value], span),
            FunctionalFieldShape.Nested => MakeCurriedPathCall("Foldable.fold_left", [value, accumulator,
                MakeLambda("nestedAcc", MakeLambda("nestedValue", BuildFoldableStep(analysis.NestedType!, MakeIdent("nestedAcc", span), MakeIdent("nestedValue", span), reducer, mappedName, span), span), span)], span),
            _ => accumulator
        };
    }

    private EidosAstNode BuildTraversableValue(TypeNode type, EidosAstNode value, EidosAstNode mapper, string mappedName, SourceSpan span)
    {
        var analysis = AnalyzeFunctionalField(type, mappedName);
        return analysis.Shape switch
        {
            FunctionalFieldShape.Direct => MakeCurriedCall(mapper, [value], span),
            FunctionalFieldShape.Nested => MakeCurriedPathCall("Traversable.traverse", [value,
                MakeLambda("inner", BuildTraversableValue(analysis.NestedType!, MakeIdent("inner", span), mapper, mappedName, span), span)], span),
            _ => MakePreludeInternalCall("Traversable", "lift_pure", [value], span)
        };
    }

    private void RegisterGeneratedTraversableConstructorHelpers(AdtDef adt, int mappedParameterIndex, SourceSpan span)
    {
        var mappedName = adt.TypeParams[mappedParameterIndex].Name;
        for (var constructorIndex = 0; constructorIndex < adt.Constructors.Count; constructorIndex++)
        {
            var ctor = adt.Constructors[constructorIndex];
            var fields = EnumerateConstructorFields(ctor).ToList();
            if (fields.Count == 0)
                continue;

            var helper = new FuncDef();
            helper.SetName(CreateTraversableConstructorHelperName(adt, ctor, constructorIndex));
            helper.SetSpan(span);
            helper.TypeParams.Add(CreateGeneratedTypeParam("B", span));
            foreach (var parameter in adt.TypeParams.Take(mappedParameterIndex))
                helper.TypeParams.Add(CreateDerivedTypeParam(parameter, null, false, span));

            var fieldTypes = fields
                .Select(field => SubstituteFunctionalMappedType(field.Type!, mappedName, "B", span))
                .ToList();
            helper.SetSignature(CreateCurriedArrowType(
                fieldTypes,
                CreateDerivedAppliedType(adt, mappedParameterIndex, "B", span),
                span));

            var vars = MakeVarPatterns(fields.Count, "field", span);
            var branch = new PatternBranch();
            branch.SetParameterPatterns(vars.Cast<Pattern>().ToList());
            branch.SetExpression(MakeCtorExpr(ctor, vars.Select(variable => (EidosAstNode)MakeIdent(variable.Name, span)).ToList(), span));
            helper.SetBody([branch]);
            RegisterGeneratedDerivedHelper(helper);
        }

    }

    private void RegisterGeneratedDerivedHelper(FuncDef helper)
    {
        if (TryGetCurrentModuleDecl() is { } module)
            module.Declarations.Add(helper);
        CollectDeclaration(helper);
    }

    private static string CreateTraversableConstructorHelperName(AdtDef adt, Constructor ctor, int constructorIndex) =>
        $"derivedTraversableConstructor_{SanitizeDerivedInstanceNameSegment(adt.Name)}_{SanitizeDerivedInstanceNameSegment(ctor.Name)}_{constructorIndex}";

    private static TypeNode SubstituteFunctionalMappedType(TypeNode type, string mappedName, string replacementName, SourceSpan span)
    {
        if (!TypeUsesTypeParameter(type, mappedName))
            return CloneTypeNode(type);

        if (type is TypePath path)
        {
            if (IsMappedType(path, mappedName))
            {
                var replacement = CreateTypePath(replacementName);
                replacement.SetSpan(span);
                return replacement;
            }

            var substituted = CreateTypePath(path.TypeName);
            substituted.SetPackageAlias(path.PackageAlias);
            substituted.ModulePath = [.. path.ModulePath];
            substituted.SetSpan(path.Span);
            substituted.TypeArgs.AddRange(path.TypeArgs.Select(argument =>
                SubstituteFunctionalMappedType(argument, mappedName, replacementName, span)));
            return substituted;
        }

        return CloneTypeNode(type);
    }

    private static IEnumerable<Field> EnumerateConstructorFields(Constructor ctor)
    {
        foreach (var type in ctor.PositionalArgs)
        {
            var field = new Field();
            field.SetType(type);
            yield return field;
        }

        foreach (var field in ctor.NamedArgs)
            yield return field;
    }

    private static LambdaExpr MakeLambda(string name, EidosAstNode body, SourceSpan span)
    {
        var lambda = new LambdaExpr();
        lambda.SetSpan(span);
        lambda.AddParameter(MakeNamedVarPattern(name, span));
        lambda.SetBody(body);
        return lambda;
    }

    private static LambdaExpr MakeApplyFunctionLambda(SourceSpan span) =>
        MakeLambda(
            "function",
            MakeLambda(
                "value",
                MakeCurriedCall(MakeIdent("function", span), [MakeIdent("value", span)], span),
                span),
            span);

    private static TupleExpr MakeTupleExpr(IReadOnlyList<EidosAstNode> elements, SourceSpan span)
    {
        var tuple = new TupleExpr();
        tuple.Span = span;
        tuple.Elements.AddRange(elements);
        return tuple;
    }

    private static EidosAstNode MakeConstructorLambda(Constructor constructor, int fieldCount, SourceSpan span)
    {
        var fields = Enumerable.Range(0, fieldCount)
            .Select(index => $"field{index}")
            .ToArray();
        EidosAstNode body = MakeCtorExpr(
            constructor,
            fields.Select(name => (EidosAstNode)MakeIdent(name, span)).ToList(),
            span);
        for (var index = fields.Length - 1; index >= 0; index--)
            body = MakeLambda(fields[index], body, span);

        return body;
    }

    private static VarPattern MakeNamedVarPattern(string name, SourceSpan span)
    {
        var pattern = new VarPattern();
        pattern.SetName(name);
        pattern.SetSpan(span);
        return pattern;
    }

    private static CallExpr MakeCurriedPathCall(string path, IReadOnlyList<EidosAstNode> args, SourceSpan span, EidosAstNode? functionOverride = null)
    {
        EidosAstNode function = functionOverride ?? MakePathValue(path, span);
        return MakeCurriedCall(function, args, span);
    }

    private CallExpr MakePreludeInternalCall(
        string moduleName,
        string functionName,
        IReadOnlyList<EidosAstNode> args,
        SourceSpan span)
    {
        return MakeCurriedCall(MakePreludeInternalValue(moduleName, functionName, span), args, span);
    }

    private PathExpr MakePreludeInternalValue(string moduleName, string functionName, SourceSpan span)
    {
        var moduleId = _symbolTable.Modules.LookupModuleByPath(
            PreludeCoreImageRegistry.PackageAlias,
            [moduleName]);
        var function = moduleId is { IsValid: true }
            ? _symbolTable.Modules.GetModuleMembers(moduleId.Value)
                .Select(_symbolTable.GetSymbol)
                .OfType<FuncSymbol>()
                .FirstOrDefault(symbol => string.Equals(symbol.Name, functionName, StringComparison.Ordinal))
            : null;
        if (function == null)
            throw new InvalidOperationException($"Missing prelude internal helper '{moduleName}.{functionName}'.");

        var path = MakePathValue($"{moduleName}.{functionName}", span);
        path.AttachSyntaxIdentity(new SyntaxIdentity(
            SyntaxIdentityKind.Identifier,
            Eidosc.Types.MetaComptimeIntrinsics.CreateStableIdentity(function, _symbolTable),
            function.Id,
            function.TypeId,
            "function"));
        return path;
    }

    private static CallExpr MakeCurriedCall(EidosAstNode function, IReadOnlyList<EidosAstNode> args, SourceSpan span)
    {
        CallExpr? current = null;
        foreach (var arg in args)
        {
            var call = new CallExpr();
            call.SetSpan(span);
            call.SetFunction(function);
            call.AddPositionalArg(arg);
            function = call;
            current = call;
        }

        return current ?? throw new InvalidOperationException("A curried call requires at least one argument.");
    }

    private static PathExpr MakePathValue(string qualifiedPath, SourceSpan span)
    {
        var segments = qualifiedPath.Split(WellKnownStrings.Separators.Path);
        var path = new PathExpr();
        path.SetSpan(span);
        path.SetPackageAlias(PreludeCoreImageRegistry.PackageAlias);
        path.SetModulePath(segments.Take(segments.Length - 1).ToList());
        path.SetName(segments[^1]);
        return path;
    }

    private static TypePath CreateAppliedType(TypeNode constructor, TypeNode argument, SourceSpan span)
    {
        if (constructor is not TypePath path)
            throw new InvalidOperationException("Derived functional constructor must be a type path.");
        var applied = new TypePath();
        applied.SetSpan(span);
        applied.SetTypeName(path.TypeName);
        applied.SetPackageAlias(path.PackageAlias);
        applied.ModulePath = [.. path.ModulePath];
        applied.TypeArgs.Add(argument);
        return applied;
    }

    private static TypePath CreateDerivedAppliedType(AdtDef adt, int mappedParameterIndex, string mappedName, SourceSpan span)
    {
        var applied = CreateTypePath(adt.Name);
        applied.SetSpan(span);
        for (var index = 0; index < adt.TypeParams.Count; index++)
        {
            var arg = CreateTypePath(index == mappedParameterIndex ? mappedName : adt.TypeParams[index].Name);
            arg.SetSpan(span);
            applied.TypeArgs.Add(arg);
        }

        return applied;
    }

    private static TypePath CreateDerivedTargetType(AdtDef adt, int mappedParameterIndex, SourceSpan span)
    {
        var target = CreateTypePath(adt.Name);
        target.SetSpan(span);
        for (var index = 0; index < mappedParameterIndex; index++)
        {
            var arg = CreateTypePath(adt.TypeParams[index].Name);
            arg.SetSpan(span);
            target.TypeArgs.Add(arg);
        }

        return target;
    }

    private static List<TypeNode> CreateFunctionalMethodParameterTypes(AdtDef adt, string traitName, int mappedParameterIndex, SourceSpan span)
    {
        var receiver = CreateDerivedAppliedType(adt, mappedParameterIndex, "A", span);
        return traitName switch
        {
            "Functor" or "Traversable" => [receiver, CreateEffectfulArrowType(CreateTypePath("A"), traitName == "Traversable" ? CreateAppliedType(CreateTypePath("G"), CreateTypePath("B"), span) : CreateTypePath("B"), "E", span)],
            "Foldable" => [receiver, CreateTypePath("B"), CreateArrowType(CreateTypePath("B"), CreateEffectfulArrowType(CreateTypePath("A"), CreateTypePath("B"), "E", span), span)],
            _ => []
        };
    }

    private static ArrowType CreateEffectfulArrowType(TypeNode parameter, TypeNode result, string effectName, SourceSpan span)
    {
        var arrow = CreateArrowType(parameter, result, span);
        arrow.SetRequiredEffects([new EffectRequirementNode { Path = [effectName], Span = span }]);
        return arrow;
    }

    private static TypeParam CreateGeneratedTypeParam(string name, SourceSpan span)
    {
        var parameter = new TypeParam();
        SetPrivate(parameter, "Name", name);
        SetPrivate(parameter, "Span", span);
        return parameter;
    }

    private static TypeParam CreateGeneratedKindTypeParam(string name, string kindText, SourceSpan span)
    {
        var parameter = CreateGeneratedTypeParam(name, span);
        var kind = new Kind { IsStar = true, KindText = kindText, Span = span };
        SetPrivate(parameter, "KindAnnotation", kind);
        var trait = new TraitRef();
        trait.SetTraitName("Applicative");
        trait.SetSpan(span);
        trait.TypeArgs.Add(CreateTypePath(name));
        parameter.TraitConstraints.Add(trait);
        return parameter;
    }

    private static TypeParam CreateGeneratedEffectTypeParam(string name, SourceSpan span)
    {
        var parameter = CreateGeneratedTypeParam(name, span);
        SetPrivate(parameter, "IsEffectSet", true);
        return parameter;
    }
}
