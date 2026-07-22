using Eidosc.Symbols;
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void ProcessDeclarationMetaClauses(
        Declaration declaration,
        AdtDef? deriveShape,
        string targetName,
        IReadOnlyList<string> targetPath)
    {
        foreach (var invocation in declaration.MetaInvocations
                     .OrderBy(static invocation => invocation.SourceOrder)
                     .ThenBy(static invocation => invocation.OccurrenceId.ArgumentSubIndex))
        {
            if (invocation.SourceOrder < 0 || invocation.SourceOrder >= declaration.Clauses.Count)
            {
                continue;
            }

            _metaInvocationOccurrences.Add(new MetaInvocationOccurrence(
                declaration,
                deriveShape,
                targetName,
                targetPath,
                _currentModule,
                declaration.Clauses[invocation.SourceOrder],
                invocation,
                [],
                CreateMetaOrderingDomainIdentity(declaration)));
        }
    }

    private void ProcessDeclarationMetaClauses(AdtDef adt)
    {
        if (adt.IsTypeAlias)
        {
            return;
        }

        ProcessDeclarationMetaClauses(adt, adt, adt.Name, [adt.Name]);

        foreach (var caseType in adt.Cases)
        {
            ProcessCaseMetaClauses(adt, caseType, [adt.Name, caseType.Name]);
        }
    }

    private static string GetMetaTargetName(Declaration declaration) => declaration switch
    {
        AdtDef adt => adt.Name,
        CaseTypeDef caseType => caseType.Name,
        FuncDef function => function.Name,
        FuncDecl function => function.Name,
        TraitDef trait => trait.Name,
        EffectDef effect => effect.Name,
        InstanceDecl instance => instance.Name,
        ModuleDecl module => string.Join(WellKnownStrings.Separators.Path, module.Path),
        LetDecl { Pattern: VarPattern variable } => variable.Name,
        _ => declaration.GetType().Name
    };

    private void ProcessCaseMetaClauses(
        AdtDef root,
        CaseTypeDef caseType,
        IReadOnlyList<string> targetPath)
    {
        var deriveShape = CreateCaseDeriveShape(root, caseType);
        foreach (var invocation in caseType.MetaInvocations
                     .OrderBy(static invocation => invocation.SourceOrder)
                     .ThenBy(static invocation => invocation.OccurrenceId.ArgumentSubIndex))
        {
            if (invocation.SourceOrder < 0 || invocation.SourceOrder >= caseType.Clauses.Count)
            {
                continue;
            }

            _metaInvocationOccurrences.Add(new MetaInvocationOccurrence(
                caseType,
                deriveShape,
                string.Join(WellKnownStrings.Separators.Path, targetPath),
                targetPath,
                _currentModule,
                caseType.Clauses[invocation.SourceOrder],
                invocation,
                [],
                CreateMetaOrderingDomainIdentity(caseType)));
        }

        foreach (var child in caseType.Cases)
        {
            ProcessCaseMetaClauses(root, child, [.. targetPath, child.Name]);
        }
    }

    private string CreateMetaOrderingDomainIdentity(Declaration declaration)
    {
        if (declaration.GeneratedOriginChain.LastOrDefault() is { } generatedOrigin)
        {
            return $"generated:{generatedOrigin.StableIdentity}";
        }
        if (declaration.SymbolId.IsValid && _symbolTable.GetSymbol(declaration.SymbolId) is { } symbol)
        {
            return MetaComptimeIntrinsics.CreateStableIdentity(symbol, _symbolTable);
        }

        return DeclarationClauseBinder.CreateDeclarationIdentity(declaration);
    }

    private static AdtDef CreateCaseDeriveShape(AdtDef root, CaseTypeDef caseType)
    {
        var descendantConstructorIds = EnumerateLeafCases(caseType)
            .Select(static leaf => leaf.ConstructorSymbolId)
            .Where(static id => id.IsValid)
            .ToHashSet();
        var shape = new AdtDef();
        shape.SetName(caseType.Name);
        shape.SetSpan(caseType.Span);
        shape.SetTypeParams([.. caseType.TypeParams]);
        shape.SetConstructors(root.Constructors
            .Where(constructor => descendantConstructorIds.Contains(constructor.SymbolId))
            .ToList());
        shape.SymbolId = caseType.SymbolId;
        return shape;

        static IEnumerable<CaseTypeDef> EnumerateLeafCases(CaseTypeDef current)
        {
            if (current.IsLeaf)
            {
                yield return current;
                yield break;
            }

            foreach (var child in current.Cases)
            {
                foreach (var leaf in EnumerateLeafCases(child))
                {
                    yield return leaf;
                }
            }
        }
    }

    private void RegisterGeneratedDerivedInstance(InstanceDecl instance)
    {
        AddCounter("Namer.collect.derivedInstance.count");
        if (TryGetCurrentModuleDecl() is { } module)
        {
            module.Declarations.Add(instance);
        }

        CollectDeclaration(instance);
    }

    private static bool ContainsEffectfulTypeNode(TypeNode type)
    {
        return type switch
        {
            EffectfulType => true,
            ArrowType arrow => ContainsEffectfulTypeNode(arrow.ParamType) || ContainsEffectfulTypeNode(arrow.ReturnType),
            TupleType tuple => tuple.Elements.Any(ContainsEffectfulTypeNode),
            TypePath path => path.TypeArgs.Any(ContainsEffectfulTypeNode),
            _ => false
        };
    }

    private static int GetConstructorRuntimeFieldCount(Constructor ctor)
    {
        return ctor.PositionalArgs.Count + ctor.NamedArgs.Count;
    }

    private InstanceDecl? GenerateDerivedImpl(
        AdtDef adt,
        string traitName,
        SourceSpan span,
        IReadOnlyList<string>? targetPath = null)
    {
        AddCounter($"Namer.collect.derive.{traitName}.count");
        var funcName = traitName switch
        {
            "Eq" => "eq",
            "Show" => "show",
            "Ord" => "compare",
            "Hash" => "hash",
            "Clone" => "clone",
            "Copy" => "_copy_marker",
            _ => null
        };

        if (funcName == null)
            return null;

        if (adt.Constructors.Count == 0)
        {
            AddError(span, DiagnosticMessages.DeriveTypeHasNoConstructors(traitName, adt.Name));
            return null;
        }

        var derivedTypeParams = new List<TypeParam>(adt.TypeParams.Count);
        var derivedMethodTypeParams = new List<TypeParam>(adt.TypeParams.Count);
        var requiredConstraint = GetDeriveRequiredConstraint(traitName);
        foreach (var tp in adt.TypeParams)
        {
            var derivedTp = CreateDerivedTypeParam(
                tp,
                requiredConstraint,
                traitName != "Copy" || AdtUsesTypeParameter(adt, tp.Name),
                span);
            derivedTypeParams.Add(derivedTp);
            derivedMethodTypeParams.Add(CreateDerivedTypeParam(
                tp,
                requiredConstraint,
                traitName != "Copy" || AdtUsesTypeParameter(adt, tp.Name),
                span));
        }

        if (traitName == "Copy")
        {
            var markerTrait = new TraitRef();
            markerTrait.SetTraitName(traitName);
            markerTrait.SetSpan(span);

            var markerInstance = new InstanceDecl();
            markerInstance.SetName(CreateDerivedInstanceName(traitName, adt.Name, targetPath));
            markerInstance.SetSpan(span);
            markerInstance.SetTypeParams(derivedTypeParams);
            markerInstance.SetTrait(markerTrait);
            markerInstance.SetTargetType(CreateAdtSelfType(adt, span, targetPath));
            return markerInstance;
        }

        var funcDef = new FuncDef();
        SetPrivate(funcDef, "Name", funcName);
        funcDef.TypeParams.AddRange(derivedMethodTypeParams);

        var returnType = traitName switch
        {
            "Eq" => CreateTypePath("Bool"),
            "Show" => CreateTypePath("String"),
            "Ord" => CreateTypePath("Ordering"),
            "Hash" => CreateTypePath("Int"),
            "Clone" => CreateAdtSelfType(adt, span, targetPath),
            _ => null
        };

        if (returnType != null)
        {
            var receiverType = traitName == "Clone"
                ? CreateRefType(CreateAdtSelfType(adt, span, targetPath), span)
                : CreateAdtSelfType(adt, span, targetPath);
            var paramTypes = new List<TypeNode> { receiverType };
            if (traitName is "Eq" or "Ord")
            {
                paramTypes.Add(CreateAdtSelfType(adt, span, targetPath));
            }

            funcDef.SetSignature(CreateCurriedArrowType(paramTypes, returnType, span));
        }

        var branches = GenerateDerivedBranches(adt, traitName, span);
        foreach (var branch in branches)
            funcDef.Body.Add(branch);

        if (funcDef.Body.Count == 0)
            return null;

        var trait = new TraitRef();
        trait.SetTraitName(traitName);
        trait.SetSpan(span);

        var instance = new InstanceDecl();
        instance.SetName(CreateDerivedInstanceName(traitName, adt.Name, targetPath));
        instance.SetSpan(span);
        instance.SetTypeParams(derivedTypeParams);
        instance.SetTrait(trait);
        instance.SetMethods([funcDef]);
        instance.SetMembers([funcDef]);
        return instance;
    }

    private static string CreateDerivedInstanceName(
        string traitName,
        string typeName,
        IReadOnlyList<string>? targetPath)
    {
        var path = targetPath is { Count: > 0 } ? targetPath : [typeName];
        return $"Derived{traitName}{string.Concat(path.Select(static segment =>
            string.IsNullOrEmpty(segment)
                ? string.Empty
                : char.ToUpperInvariant(segment[0]) + segment[1..]))}";
    }

    private static string? GetDeriveRequiredConstraint(string traitName)
    {
        return traitName switch
        {
            "Eq" => "Eq",
            "Show" => "Show",
            "Ord" => "Ord",
            "Hash" => "Hash",
            "Clone" => "Clone",
            "Copy" => "Copy",
            _ => null
        };
    }

    private static TypeParam CreateDerivedTypeParam(
        TypeParam original,
        string? requiredConstraint,
        bool addRequiredConstraint,
        SourceSpan span)
    {
        var derived = new TypeParam();
        SetPrivate(derived, "Name", original.Name);
        SetPrivate(derived, "Span", original.Span);

        if (original.KindAnnotation != null)
            SetPrivate(derived, "KindAnnotation", original.KindAnnotation);

        foreach (var constraint in original.TraitConstraints)
            derived.TraitConstraints.Add(constraint);

        if (addRequiredConstraint &&
            requiredConstraint != null &&
            !derived.TraitConstraints.Any(c =>
                string.Equals(c.TraitName, requiredConstraint, StringComparison.Ordinal)))
        {
            var constraintRef = new TraitRef();
            constraintRef.SetSpan(span);
            constraintRef.SetTraitName(requiredConstraint);
            derived.TraitConstraints.Add(constraintRef);
        }

        return derived;
    }

    private static bool AdtUsesTypeParameter(AdtDef adt, string typeParameterName)
    {
        foreach (var field in adt.Fields)
        {
            if (TypeUsesTypeParameter(field.Type, typeParameterName))
            {
                return true;
            }
        }

        foreach (var constructor in adt.Constructors)
        {
            if (constructor.PositionalArgs.Any(type => TypeUsesTypeParameter(type, typeParameterName)) ||
                constructor.NamedArgs.Any(field => TypeUsesTypeParameter(field.Type, typeParameterName)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TypeUsesTypeParameter(TypeNode? type, string typeParameterName)
    {
        return type switch
        {
            null => false,
            TypePath path => string.Equals(path.TypeName, typeParameterName, StringComparison.Ordinal) ||
                             path.TypeArgs.Any(argument => TypeUsesTypeParameter(argument, typeParameterName)) ||
                             path.GenericArguments.Any(argument => argument switch
                             {
                                 TypeGenericArgumentNode typed => TypeUsesTypeParameter(typed.Type, typeParameterName),
                                 UnresolvedGenericArgumentNode unresolved => TypeUsesTypeParameter(unresolved.TypeCandidate, typeParameterName),
                                 _ => false
                             }),
            TupleType tuple => tuple.Elements.Any(element => TypeUsesTypeParameter(element, typeParameterName)),
            ArrowType arrow => TypeUsesTypeParameter(arrow.ParamType, typeParameterName) ||
                               TypeUsesTypeParameter(arrow.ReturnType, typeParameterName),
            EffectfulType effectful => TypeUsesTypeParameter(effectful.InputType, typeParameterName) ||
                                       TypeUsesTypeParameter(effectful.OutputType, typeParameterName),
            _ => false
        };
    }

    private List<PatternBranch> GenerateDerivedBranches(AdtDef adt, string traitName, SourceSpan span)
    {
        return traitName switch
        {
            "Eq" => GenerateEqBranches(adt, span),
            "Show" => GenerateShowBranches(adt, span),
            "Ord" => GenerateOrdBranches(adt, span),
            "Hash" => GenerateHashBranches(adt, span),
            "Clone" => GenerateCloneBranches(adt, span),
            "Copy" => [GenerateCopyBranch(span)],
            _ => []
        };
    }

    #region Eq Generation

    private List<PatternBranch> GenerateEqBranches(AdtDef adt, SourceSpan span)
    {
        var branches = new List<PatternBranch>();

        if (adt.Constructors.Count == 1)
        {
            var ctor = adt.Constructors[0];
            var fieldCount = ctor.PositionalArgs.Count + ctor.NamedArgs.Count;
            var leftVars = MakeVarPatterns(fieldCount, "l", span);
            var rightVars = MakeVarPatterns(fieldCount, "r", span);
            var leftPat = MakeCtorPattern(ctor, leftVars, span);
            var rightPat = MakeCtorPattern(ctor, rightVars, span);
            var tuplePat = MakeTuplePattern([leftPat, rightPat], span);
            var expr = fieldCount == 0
                ? CreateBoolLiteral("true", span)
                : BuildEqChain(leftVars, rightVars, span);
            branches.Add(MakeBranch(tuplePat, expr, span));
        }
        else
        {
            for (var i = 0; i < adt.Constructors.Count; i++)
            {
                var ctor = adt.Constructors[i];
                var fieldCount = ctor.PositionalArgs.Count + ctor.NamedArgs.Count;
                var leftVars = MakeVarPatterns(fieldCount, "l", span);
                var rightVars = MakeVarPatterns(fieldCount, "r", span);
                var leftPat = MakeCtorPattern(ctor, leftVars, span);
                var rightPat = MakeCtorPattern(ctor, rightVars, span);
                var tuplePat = MakeTuplePattern([leftPat, rightPat], span);
                var expr = fieldCount == 0
                    ? CreateBoolLiteral("true", span)
                    : BuildEqChain(leftVars, rightVars, span);
                branches.Add(MakeBranch(tuplePat, expr, span));
            }

            branches.Add(MakeBranch(new WildcardPattern(), CreateBoolLiteral("false", span), span));
        }

        return branches;
    }

    #endregion

    #region Show Generation

    private List<PatternBranch> GenerateShowBranches(AdtDef adt, SourceSpan span)
    {
        var branches = new List<PatternBranch>();

        foreach (var ctor in adt.Constructors)
        {
            var fieldCount = ctor.PositionalArgs.Count + ctor.NamedArgs.Count;
            var vars = MakeVarPatterns(fieldCount, "v", span);
            var pat = MakeCtorPattern(ctor, vars, span);
            var expr = BuildShowExpr(ctor.Name, vars, span);
            branches.Add(MakeBranch(pat, expr, span));
        }

        return branches;
    }

    #endregion

    #region Ord Generation

    private List<PatternBranch> GenerateOrdBranches(AdtDef adt, SourceSpan span)
    {
        var branches = new List<PatternBranch>();

        // Same-constructor branches: (C_i(l0,...), C_i(r0,...)) => compare fields
        for (var i = 0; i < adt.Constructors.Count; i++)
        {
            var ctor = adt.Constructors[i];
            var fieldCount = ctor.PositionalArgs.Count + ctor.NamedArgs.Count;
            var leftVars = MakeVarPatterns(fieldCount, "l", span);
            var rightVars = MakeVarPatterns(fieldCount, "r", span);
            var leftPat = MakeCtorPattern(ctor, leftVars, span);
            var rightPat = MakeCtorPattern(ctor, rightVars, span);
            var tuplePat = MakeTuplePattern([leftPat, rightPat], span);
            var expr = fieldCount == 0
                ? MakePathCall("std.Ordering.Equal", [], span)
                : BuildOrdChain(leftVars, rightVars, span);
            branches.Add(MakeBranch(tuplePat, expr, span));
        }

        // Multi-constructor: for each ctor C_i, (C_i, _) => Less
        // Remaining unhandled cases are (C_j where j > i, C_i where j < i) => Greater
        if (adt.Constructors.Count > 1)
        {
            for (var i = 0; i < adt.Constructors.Count; i++)
            {
                var ctor = adt.Constructors[i];
                var leftPat = MakeCtorPattern(ctor, [], span);
                var wildcardPat = new WildcardPattern();
                SetPrivate(wildcardPat, "Span", span);
                var tuplePat = MakeTuplePattern([leftPat, wildcardPat], span);
                branches.Add(MakeBranch(tuplePat, MakePathCall("std.Ordering.Less", [], span), span));
            }

            branches.Add(MakeBranch(new WildcardPattern(), MakePathCall("std.Ordering.Greater", [], span), span));
        }

        return branches;
    }

    #endregion

    #region Hash Generation

    private List<PatternBranch> GenerateHashBranches(AdtDef adt, SourceSpan span)
    {
        var branches = new List<PatternBranch>();

        foreach (var ctor in adt.Constructors)
        {
            var fieldCount = ctor.PositionalArgs.Count + ctor.NamedArgs.Count;
            var vars = MakeVarPatterns(fieldCount, "v", span);
            var pat = MakeCtorPattern(ctor, vars, span);

            // Mix constructor ordinal with field hashes to distinguish different constructors
            var ctorHash = MakeIntLiteral(StableConstructorHash(ctor.Name).ToString(), span);
            if (fieldCount == 0)
            {
                branches.Add(MakeBranch(pat, ctorHash, span));
            }
            else
            {
                var fieldHash = BuildHashChain(vars, span);
                var combined = new BinaryExpr();
                combined.SetSpan(span);
                combined.SetLeft(ctorHash);
                combined.SetOperator(BinaryOp.Add);
                combined.SetRight(fieldHash);
                branches.Add(MakeBranch(pat, combined, span));
            }
        }

        return branches;
    }

    private static long StableConstructorHash(string constructorName)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(constructorName));
        return BinaryPrimitives.ReadInt64LittleEndian(digest);
    }

    #endregion

    #region Clone Generation

    private List<PatternBranch> GenerateCloneBranches(AdtDef adt, SourceSpan span)
    {
        var branches = new List<PatternBranch>();

        foreach (var ctor in adt.Constructors)
        {
            var fieldCount = ctor.PositionalArgs.Count + ctor.NamedArgs.Count;
            var vars = MakeVarPatterns(fieldCount, "v", span);
            var pat = MakeCtorPattern(ctor, vars, span);

            var clonedFields = new List<EidosAstNode>();
            for (var i = 0; i < vars.Count; i++)
            {
                clonedFields.Add(
                    MakeTraitInvokeCall("clone_value", MakeRefExpr(MakeIdent(vars[i].Name, span)), span));
            }

            var expr = MakeCtorExpr(ctor, clonedFields, span);
            branches.Add(MakeBranch(pat, expr, span));
        }

        var receiverPattern = new VarPattern();
        receiverPattern.SetName("value");
        return [MakeBranch(
            receiverPattern,
            MakeMatchExpr(MakeDerefExpr(MakeIdent("value", span)), branches, span),
            span)];
    }

    #endregion

    #region Copy Generation

    private static PatternBranch GenerateCopyBranch(SourceSpan span)
    {
        var pat = new WildcardPattern();
        SetPrivate(pat, "Span", span);
        var expr = MakeUnitLiteral(span);
        return MakeBranch(pat, expr, span);
    }

    #endregion

    #region Expression Builders

    private EidosAstNode BuildEqChain(List<VarPattern> leftVars, List<VarPattern> rightVars, SourceSpan span)
    {
        EidosAstNode? result = null;
        for (var i = 0; i < leftVars.Count; i++)
        {
            var comparison = MakeTraitInvokeCall("eq_value",
                MakeIdent(leftVars[i].Name, span),
                MakeIdent(rightVars[i].Name, span), span);

            if (result == null)
            {
                result = comparison;
            }
            else
            {
                var bin = new BinaryExpr();
                bin.SetSpan(span);
                bin.SetLeft(result);
                bin.SetOperator(BinaryOp.And);
                bin.SetRight(comparison);
                result = bin;
            }
        }

        return result ?? CreateBoolLiteral("true", span);
    }

    private EidosAstNode BuildShowExpr(string ctorName, List<VarPattern> vars, SourceSpan span)
    {
        if (vars.Count == 0)
            return MakeStringLiteral(ctorName, span);

        var parts = new List<EidosAstNode> { MakeStringLiteral(ctorName + "(", span) };
        for (var i = 0; i < vars.Count; i++)
        {
            if (i > 0) parts.Add(MakeStringLiteral(", ", span));
            parts.Add(MakeTraitInvokeCall("show_value", MakeIdent(vars[i].Name, span), span));
        }
        parts.Add(MakeStringLiteral(")", span));

        return ConcatExprs(parts, span);
    }

    private EidosAstNode BuildOrdChain(List<VarPattern> leftVars, List<VarPattern> rightVars, SourceSpan span)
    {
        var firstCmp = MakeTraitInvokeCall("compare_value",
            MakeIdent(leftVars[0].Name, span),
            MakeIdent(rightVars[0].Name, span), span);

        if (leftVars.Count == 1)
            return firstCmp;

        EidosAstNode result = firstCmp;
        for (var i = 1; i < leftVars.Count; i++)
        {
            var nextCmp = MakeTraitInvokeCall("compare_value",
                MakeIdent(leftVars[i].Name, span),
                MakeIdent(rightVars[i].Name, span), span);

            result = MakePathCall("std.Ordering.then_with", [result, nextCmp], span);
        }

        return result;
    }

    private EidosAstNode BuildHashChain(List<VarPattern> vars, SourceSpan span)
    {
        if (vars.Count == 0)
            return MakeIntLiteral("0", span);

        EidosAstNode result = MakeTraitInvokeCall("hash_value", MakeIdent(vars[0].Name, span), span);
        for (var i = 1; i < vars.Count; i++)
        {
            var nextHash = MakeTraitInvokeCall("hash_value", MakeIdent(vars[i].Name, span), span);
            var bin = new BinaryExpr();
            bin.SetSpan(span);
            bin.SetLeft(result);
            bin.SetOperator(BinaryOp.Add);
            bin.SetRight(nextHash);
            result = bin;
        }

        return result;
    }

    private static EidosAstNode ConcatExprs(List<EidosAstNode> parts, SourceSpan span)
    {
        var result = parts[0];
        for (var i = 1; i < parts.Count; i++)
        {
            var bin = new BinaryExpr();
            bin.SetSpan(span);
            bin.SetLeft(result);
            bin.SetOperator(BinaryOp.Concat);
            bin.SetRight(parts[i]);
            result = bin;
        }
        return result;
    }

    #endregion

    #region AST Construction Helpers

    private static List<VarPattern> MakeVarPatterns(int count, string prefix, SourceSpan span)
    {
        var patterns = new List<VarPattern>();
        for (var i = 0; i < count; i++)
        {
            var vp = new VarPattern();
            vp.SetName($"{prefix}{i}");
            patterns.Add(vp);
        }
        return patterns;
    }

    private static List<Pattern> MakeWildcardPatterns(int count, SourceSpan span)
    {
        var patterns = new List<Pattern>();
        for (var i = 0; i < count; i++)
        {
            var wildcard = new WildcardPattern();
            SetPrivate(wildcard, "Span", span);
            patterns.Add(wildcard);
        }

        return patterns;
    }

    private static CtorPattern MakeCtorPattern(Constructor ctor, IReadOnlyList<Pattern> vars, SourceSpan span)
    {
        var cp = new CtorPattern();
        cp.SetConstructorName(ctor.Name);
        cp.SetSpan(span);

        // Named-field constructors must be matched/pattern-built with named patterns;
        // positional patterns are rejected for them. Fall back to positional otherwise.
        if (ctor.NamedArgs.Count > 0)
        {
            for (var i = 0; i < vars.Count && i < ctor.NamedArgs.Count; i++)
            {
                var fieldPattern = new FieldPattern();
                fieldPattern.SetSpan(span);
                fieldPattern.SetFieldName(ctor.NamedArgs[i].Name);
                fieldPattern.SetPattern(vars[i]);
                cp.AddNamedPattern(fieldPattern);
            }
        }
        else
        {
            foreach (var v in vars)
                cp.AddPositionalPattern(v);
        }

        return cp;
    }

    private static TuplePattern MakeTuplePattern(List<Pattern> elements, SourceSpan span)
    {
        var tp = new TuplePattern();
        foreach (var e in elements)
            tp.AddElement(e);
        return tp;
    }

    private static PatternBranch MakeBranch(Pattern pattern, EidosAstNode expression, SourceSpan span)
    {
        var branch = new PatternBranch();
        SetPrivate(branch, "Pattern", pattern);
        SetPrivate(branch, "Expression", expression);
        return branch;
    }

    private static IdentifierExpr MakeIdent(string name, SourceSpan span)
    {
        var id = new IdentifierExpr();
        id.SetName(name);
        return id;
    }

    private static CallExpr MakeTraitInvokeCall(string method, EidosAstNode firstArg, SourceSpan span)
    {
        var path = new PathExpr();
        SetPrivate(path, "ModulePath", new List<string> { "TraitInvoke" });
        SetPrivate(path, "Name", method);

        var call = new CallExpr();
        call.SetFunction(path);
        call.AddPositionalArg(firstArg);
        return call;
    }

    private static CallExpr MakeTraitInvokeCall(string method, EidosAstNode firstArg, EidosAstNode secondArg, SourceSpan span)
    {
        var innerPath = new PathExpr();
        SetPrivate(innerPath, "ModulePath", new List<string> { "TraitInvoke" });
        SetPrivate(innerPath, "Name", method);

        var innerCall = new CallExpr();
        innerCall.SetFunction(innerPath);
        innerCall.AddPositionalArg(firstArg);

        var outerCall = new CallExpr();
        outerCall.SetFunction(innerCall);
        outerCall.AddPositionalArg(secondArg);
        return outerCall;
    }

    private static UnaryExpr MakeRefExpr(EidosAstNode operand)
    {
        var expression = new UnaryExpr();
        expression.SetOperator(UnaryOp.Ref);
        expression.SetOperand(operand);
        return expression;
    }

    private static UnaryExpr MakeDerefExpr(EidosAstNode operand)
    {
        var expression = new UnaryExpr();
        expression.SetOperator(UnaryOp.Deref);
        expression.SetOperand(operand);
        return expression;
    }

    private static MatchExpr MakeMatchExpr(
        EidosAstNode matchedExpression,
        IReadOnlyList<PatternBranch> branches,
        SourceSpan span)
    {
        var match = new MatchExpr { Span = span };
        SetPrivate(match, "MatchedExpression", matchedExpression);
        match.Branches.AddRange(branches);
        return match;
    }

    private static CallExpr MakePathCall(string qualifiedPath, List<EidosAstNode> args, SourceSpan span)
    {
        var segments = qualifiedPath.Split(WellKnownStrings.Separators.Path);
        var path = new PathExpr();
        SetPrivate(path, "ModulePath", segments.Take(segments.Length - 1).ToList());
        SetPrivate(path, "Name", segments[^1]);

        var call = new CallExpr();
        call.SetFunction(path);
        foreach (var a in args)
            call.AddPositionalArg(a);
        return call;
    }

    private static LiteralExpr CreateBoolLiteral(string value, SourceSpan span)
    {
        var lit = new LiteralExpr();
        lit.SetLiteral(value);
        return lit;
    }

    private static LiteralExpr MakeStringLiteral(string value, SourceSpan span)
    {
        var lit = new LiteralExpr();
        lit.SetLiteral($"\"{value}\"");
        return lit;
    }

    private static LiteralExpr MakeIntLiteral(string value, SourceSpan span)
    {
        var lit = new LiteralExpr();
        lit.SetLiteral(value);
        return lit;
    }

    private static LiteralExpr MakeUnitLiteral(SourceSpan span)
    {
        var lit = new LiteralExpr();
        lit.SetLiteral("()");
        return lit;
    }

    private static TypePath CreateTypePath(string typeName)
    {
        var tp = new TypePath();
        tp.SetTypeName(typeName);
        return tp;
    }

    private static TypePath CreateRefType(TypeNode innerType, SourceSpan span)
    {
        var reference = CreateTypePath("Ref");
        reference.SetSpan(span);
        reference.TypeArgs.Add(innerType);
        return reference;
    }

    private static TypePath CreateAdtSelfType(
        AdtDef adt,
        SourceSpan span,
        IReadOnlyList<string>? targetPath = null)
    {
        var path = targetPath is { Count: > 0 } ? targetPath : [adt.Name];
        var tp = CreateTypePath(path[^1]);
        tp.ModulePath = path.Take(path.Count - 1).ToList();
        tp.SetSpan(span);
        foreach (var typeParam in adt.TypeParams)
        {
            var arg = CreateTypePath(typeParam.Name);
            arg.SetSpan(typeParam.Span);
            tp.TypeArgs.Add(arg);
        }

        return tp;
    }

    private static ArrowType CreateArrowType(TypeNode paramType, TypeNode returnType, SourceSpan span)
    {
        var arrow = new ArrowType();
        arrow.SetSpan(span);
        arrow.SetParamType(paramType);
        arrow.SetReturnType(returnType);
        return arrow;
    }

    private static TypeNode CreateCurriedArrowType(IReadOnlyList<TypeNode> paramTypes, TypeNode returnType, SourceSpan span)
    {
        var result = returnType;
        for (var i = paramTypes.Count - 1; i >= 0; i--)
        {
            result = CreateArrowType(paramTypes[i], result, span);
        }

        return result;
    }

    private static bool IsSupportedConstructorConstantExpression(EidosAstNode expr)
    {
        return expr switch
        {
            LiteralExpr => true,
            IdentifierExpr => true,
            PathExpr => true,
            CtorExpr ctor => IsSupportedConstructorExpression(ctor),
            CallExpr call => IsSupportedCallExpression(call),
            TupleExpr tuple => tuple.Elements.All(IsSupportedConstructorConstantExpression),
            ListExpr list => list.Elements.All(IsSupportedConstructorConstantExpression),
            UnaryExpr { Operator: UnaryOp.Negate or UnaryOp.Not, Operand: not null } unary =>
                IsSupportedConstructorConstantExpression(unary.Operand),
            BinaryExpr { Left: not null, Right: not null } binary when IsSupportedConstructorConstantBinaryOp(binary.Operator) =>
                IsSupportedConstructorConstantExpression(binary.Left) &&
                IsSupportedConstructorConstantExpression(binary.Right),
            _ => false
        };
    }

    private static bool IsSupportedConstructorExpression(CtorExpr ctor)
    {
        return !string.IsNullOrWhiteSpace(ctor.ConstructorName) &&
            ctor.UpdateBase == null &&
            ctor.PositionalArgs.All(IsSupportedConstructorConstantExpression) &&
            ctor.NamedArgs.All(field => field.Value != null &&
                IsSupportedConstructorConstantExpression(field.Value));
    }

    private static bool IsSupportedCallExpression(CallExpr call)
    {
        return call.Function is IdentifierExpr or PathExpr &&
            call.PositionalArgs.All(IsSupportedConstructorConstantExpression) &&
            call.NamedArgs.All(arg => arg.Value != null &&
                IsSupportedConstructorConstantExpression(arg.Value));
    }

    private static bool IsSupportedConstructorConstantBinaryOp(BinaryOp op)
    {
        return op is BinaryOp.Add
            or BinaryOp.Subtract
            or BinaryOp.Multiply
            or BinaryOp.Divide
            or BinaryOp.Modulo
            or BinaryOp.Less
            or BinaryOp.Greater
            or BinaryOp.LessEqual
            or BinaryOp.GreaterEqual
            or BinaryOp.Equal
            or BinaryOp.NotEqual
            or BinaryOp.And
            or BinaryOp.Or
            or BinaryOp.Concat;
    }

    private static TypeNode CloneTypeNode(TypeNode type)
    {
        return type switch
        {
            TypePath path => CloneTypePath(path),
            TupleType tuple => CloneTupleType(tuple),
            ArrowType arrow => CloneArrowType(arrow),
            EffectfulType effectful => CloneEffectfulType(effectful),
            _ => type
        };
    }

    private static TypeNode SubstituteSelfType(TypeNode type, TypeNode selfType)
    {
        return type switch
        {
            TypePath
            {
                PackageAlias: null,
                ModulePath.Count: 0,
                TypeArgs.Count: 0,
                TypeName: WellKnownStrings.Keywords.Self
            } => CloneTypeNode(selfType),
            TypePath path => SubstituteSelfTypePath(path, selfType),
            TupleType tuple => SubstituteSelfTupleType(tuple, selfType),
            ArrowType arrow => SubstituteSelfArrowType(arrow, selfType),
            EffectfulType effectful => SubstituteSelfEffectfulType(effectful, selfType),
            _ => CloneTypeNode(type)
        };
    }

    private static TypePath SubstituteSelfTypePath(TypePath path, TypeNode selfType)
    {
        var clone = CloneTypePath(path);
        if (path.GenericArguments.Count > 0)
        {
            clone.SetGenericArguments(path.GenericArguments.Select(argument => SubstituteSelfGenericArgument(argument, selfType)));
        }
        else
        {
            clone.TypeArgs.Clear();
            clone.TypeArgs.AddRange(path.TypeArgs.Select(arg => SubstituteSelfType(arg, selfType)));
        }

        return clone;
    }

    private static TupleType SubstituteSelfTupleType(TupleType tuple, TypeNode selfType)
    {
        var clone = new TupleType
        {
            Elements = tuple.Elements.Select(element => SubstituteSelfType(element, selfType)).ToList()
        };
        SetPrivate(clone, "Span", tuple.Span);
        return clone;
    }

    private static ArrowType SubstituteSelfArrowType(ArrowType arrow, TypeNode selfType)
    {
        var clone = new ArrowType();
        clone.SetSpan(arrow.Span);
        clone.SetParamType(SubstituteSelfType(arrow.ParamType, selfType));
        clone.SetReturnType(SubstituteSelfType(arrow.ReturnType, selfType));
        return clone;
    }

    private static EffectfulType SubstituteSelfEffectfulType(EffectfulType effectful, TypeNode selfType)
    {
        var clone = new EffectfulType
        {
            InputType = SubstituteSelfType(effectful.InputType, selfType),
            OutputType = effectful.OutputType == null ? null : SubstituteSelfType(effectful.OutputType, selfType),
            EffectPath = [.. effectful.EffectPath],
            EffectPaths = effectful.EffectPaths.Select(path => path.ToList()).ToList(),
            EffectPathSpans = [.. effectful.EffectPathSpans]
        };
        SetPrivate(clone, "Span", effectful.Span);
        return clone;
    }

    private static TypePath CloneTypePath(TypePath path)
    {
        var clone = new TypePath
        {
            ModulePath = [.. path.ModulePath],
            TypeArgs = path.TypeArgs.Select(CloneTypeNode).ToList()
        };
        clone.SetTypeName(path.TypeName);
        clone.SetPackageAlias(path.PackageAlias);
        clone.SetSpan(path.Span);
        clone.SymbolId = path.SymbolId;
        if (path.GenericArguments.Count > 0)
        {
            clone.SetGenericArguments(path.GenericArguments.Select(CloneGenericArgument));
        }

        return clone;
    }

    private static GenericArgumentNode CloneGenericArgument(GenericArgumentNode argument) => argument switch
    {
        TypeGenericArgumentNode typeArgument => new TypeGenericArgumentNode
        {
            Type = CloneTypeNode(typeArgument.Type),
            Span = typeArgument.Span
        },
        ValueGenericArgumentNode valueArgument => new ValueGenericArgumentNode
        {
            Expression = CloneExpression(valueArgument.Expression),
            Span = valueArgument.Span
        },
        EffectGenericArgumentNode effectArgument => new EffectGenericArgumentNode
        {
            EffectRow = CloneTypeNode(effectArgument.EffectRow),
            Span = effectArgument.Span
        },
        UnresolvedGenericArgumentNode unresolved => new UnresolvedGenericArgumentNode
        {
            TypeCandidate = unresolved.TypeCandidate == null ? null : CloneTypeNode(unresolved.TypeCandidate),
            ValueCandidate = unresolved.ValueCandidate == null ? null : CloneExpression(unresolved.ValueCandidate),
            Span = unresolved.Span
        },
        _ => argument
    };

    private static GenericArgumentNode SubstituteSelfGenericArgument(GenericArgumentNode argument, TypeNode selfType) =>
        argument switch
        {
            TypeGenericArgumentNode typeArgument => new TypeGenericArgumentNode
            {
                Type = SubstituteSelfType(typeArgument.Type, selfType),
                Span = typeArgument.Span
            },
            ValueGenericArgumentNode valueArgument => new ValueGenericArgumentNode
            {
                Expression = CloneExpression(valueArgument.Expression),
                Span = valueArgument.Span
            },
            EffectGenericArgumentNode effectArgument => new EffectGenericArgumentNode
            {
                EffectRow = SubstituteSelfType(effectArgument.EffectRow, selfType),
                Span = effectArgument.Span
            },
            UnresolvedGenericArgumentNode unresolved => new UnresolvedGenericArgumentNode
            {
                TypeCandidate = unresolved.TypeCandidate == null ? null : SubstituteSelfType(unresolved.TypeCandidate, selfType),
                ValueCandidate = unresolved.ValueCandidate == null ? null : CloneExpression(unresolved.ValueCandidate),
                Span = unresolved.Span
            },
            _ => argument
        };

    private static TupleType CloneTupleType(TupleType tuple)
    {
        var clone = new TupleType
        {
            Elements = tuple.Elements.Select(CloneTypeNode).ToList()
        };
        SetPrivate(clone, "Span", tuple.Span);
        return clone;
    }

    private static ArrowType CloneArrowType(ArrowType arrow)
    {
        var clone = new ArrowType();
        clone.SetSpan(arrow.Span);
        clone.SetParamType(CloneTypeNode(arrow.ParamType));
        clone.SetReturnType(CloneTypeNode(arrow.ReturnType));
        return clone;
    }

    private static EffectfulType CloneEffectfulType(EffectfulType effectful)
    {
        var clone = new EffectfulType
        {
            InputType = CloneTypeNode(effectful.InputType),
            OutputType = effectful.OutputType == null ? null : CloneTypeNode(effectful.OutputType),
            EffectPath = [.. effectful.EffectPath],
            EffectPaths = effectful.EffectPaths.Select(path => path.ToList()).ToList(),
            EffectPathSpans = [.. effectful.EffectPathSpans]
        };
        SetPrivate(clone, "Span", effectful.Span);
        return clone;
    }

    private static EidosAstNode CloneExpression(EidosAstNode expr)
    {
        return expr switch
        {
            LiteralExpr literal => CloneLiteral(literal),
            IdentifierExpr identifier => CloneIdentifier(identifier),
            PathExpr path => ClonePathExpr(path),
            CtorExpr ctor => CloneCtorExpr(ctor),
            CallExpr call => CloneCallExpr(call),
            TupleExpr tuple => new TupleExpr { Elements = tuple.Elements.Select(CloneExpression).ToList() },
            ListExpr list => CloneListExpr(list),
            UnaryExpr unary => CloneUnaryExpr(unary),
            BinaryExpr binary => CloneBinaryExpr(binary),
            _ => expr
        };
    }

    private static IdentifierExpr CloneIdentifier(IdentifierExpr identifier)
    {
        var clone = new IdentifierExpr
        {
            SymbolId = identifier.SymbolId,
            InferredType = identifier.InferredType,
            IsConstructor = identifier.IsConstructor
        };
        clone.SetSpan(identifier.Span);
        clone.SetName(identifier.Name);
        foreach (var candidate in identifier.ValueCandidateSymbolIds)
        {
            clone.AddValueCandidate(candidate);
        }

        return clone;
    }

    private static PathExpr ClonePathExpr(PathExpr path)
    {
        var clone = new PathExpr
        {
            SymbolId = path.SymbolId,
            InferredType = path.InferredType
        };
        clone.SetSpan(path.Span);
        clone.SetName(path.Name);
        clone.SetPackageAlias(path.PackageAlias);
        clone.SetModulePath([.. path.ModulePath]);
        clone.SetIsTypePath(path.IsTypePath);
        clone.SetTypeArgs(path.TypeArgs.Select(CloneTypeNode).ToList());
        foreach (var candidate in path.ValueCandidateSymbolIds)
        {
            clone.AddValueCandidate(candidate);
        }

        return clone;
    }

    private static CtorExpr CloneCtorExpr(CtorExpr ctor)
    {
        var clone = new CtorExpr
        {
            SymbolId = ctor.SymbolId,
            InferredType = ctor.InferredType
        };
        clone.SetSpan(ctor.Span);
        if (ctor.ConstructorPath != null)
        {
            clone.SetConstructorPath(CloneTypePath(ctor.ConstructorPath));
        }
        else
        {
            clone.SetConstructorName(ctor.ConstructorName);
        }

        foreach (var arg in ctor.PositionalArgs)
        {
            clone.AddPositionalArg(CloneExpression(arg));
        }

        foreach (var field in ctor.NamedArgs)
        {
            clone.AddNamedArg(CloneFieldInit(field));
        }

        if (ctor.UpdateBase != null)
        {
            clone.SetUpdateBase(CloneExpression(ctor.UpdateBase));
        }

        return clone;
    }

    private static CallExpr CloneCallExpr(CallExpr call)
    {
        var clone = new CallExpr
        {
            SymbolId = call.SymbolId,
            InferredType = call.InferredType
        };
        clone.SetSpan(call.Span);
        if (call.Function != null)
        {
            clone.SetFunction(CloneExpression(call.Function));
        }

        foreach (var arg in call.PositionalArgs)
        {
            clone.AddPositionalArg(CloneExpression(arg));
        }

        foreach (var arg in call.NamedArgs)
        {
            clone.AddNamedArg(CloneNamedArg(arg));
        }

        if (call.SynthesizedUnitArgumentCount > 0)
        {
            clone.MarkSyntheticUnitArguments(call.SynthesizedUnitArgumentCount);
        }
        else if (call.UsesFfiUnitArgumentElision)
        {
            clone.MarkFfiUnitArgumentElision();
        }

        return clone;
    }

    private static LiteralExpr CloneLiteral(LiteralExpr literal)
    {
        var clone = new LiteralExpr();
        clone.SetSpan(literal.Span);
        clone.SetLiteral(literal.RawText);
        return clone;
    }

    private static FieldInit CloneFieldInit(FieldInit field)
    {
        var clone = new FieldInit
        {
            SymbolId = field.SymbolId,
            InferredType = field.InferredType
        };
        clone.SetSpan(field.Span);
        clone.SetFieldName(field.FieldName);
        if (field.Value != null)
        {
            clone.SetValue(CloneExpression(field.Value));
        }

        return clone;
    }

    private static NamedArg CloneNamedArg(NamedArg arg)
    {
        return new NamedArg
        {
            SymbolId = arg.SymbolId,
            InferredType = arg.InferredType,
            Name = arg.Name,
            Value = arg.Value == null ? null : CloneExpression(arg.Value)
        };
    }

    private static ListExpr CloneListExpr(ListExpr list)
    {
        var clone = new ListExpr();
        clone.SetSpan(list.Span);
        foreach (var element in list.Elements)
        {
            clone.AddElement(CloneExpression(element));
        }

        clone.SetHasRest(list.HasRest);
        return clone;
    }

    private static UnaryExpr CloneUnaryExpr(UnaryExpr unary)
    {
        var clone = new UnaryExpr();
        clone.SetSpan(unary.Span);
        clone.SetOperator(unary.Operator);
        if (unary.Operand != null)
        {
            clone.SetOperand(CloneExpression(unary.Operand));
        }

        return clone;
    }

    private static BinaryExpr CloneBinaryExpr(BinaryExpr binary)
    {
        var clone = new BinaryExpr();
        clone.SetSpan(binary.Span);
        clone.SetOperator(binary.Operator);
        if (binary.Left != null)
        {
            clone.SetLeft(CloneExpression(binary.Left));
        }

        if (binary.Right != null)
        {
            clone.SetRight(CloneExpression(binary.Right));
        }

        return clone;
    }

    private static CtorExpr MakeCtorExpr(Constructor ctor, List<EidosAstNode> args, SourceSpan span)
    {
        var ctorExpr = new CtorExpr();
        SetPrivate(ctorExpr, "ConstructorName", ctor.Name);
        SetPrivate(ctorExpr, "Span", span);

        // Named-field constructors must be built with named field initializers.
        if (ctor.NamedArgs.Count > 0)
        {
            for (var i = 0; i < args.Count && i < ctor.NamedArgs.Count; i++)
            {
                var fieldInit = new FieldInit();
                fieldInit.SetSpan(span);
                fieldInit.SetFieldName(ctor.NamedArgs[i].Name);
                fieldInit.SetValue(args[i]);
                ctorExpr.AddNamedArg(fieldInit);
            }
        }
        else
        {
            foreach (var arg in args)
                ctorExpr.PositionalArgs.Add(arg);
        }

        return ctorExpr;
    }

    private static void SetPrivate(object obj, string propName, object value)
    {
        var prop = obj.GetType().GetProperty(propName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop?.CanWrite == true)
        {
            prop.SetValue(obj, value);
            return;
        }

        var field = obj.GetType().GetField($"<{propName}>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(obj, value);
    }

    #endregion
}
