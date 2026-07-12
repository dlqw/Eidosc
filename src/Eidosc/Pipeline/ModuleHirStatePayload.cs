using System.Globalization;
using Eidosc.Hir;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using ConstructorTypeLayout = Eidosc.Mir.ConstructorTypeLayout;
using ParameterEffectMap = Eidosc.Mir.ParameterEffectMap;

namespace Eidosc.Pipeline;

public sealed record ModuleHirStatePayload(
    string SchemaVersion,
    HirStateModulePayload? Module,
    ModuleHirAttachedStatePayload AttachedState,
    int UnsupportedNodeCount,
    IReadOnlyList<string> UnsupportedNodeKinds,
    string Hash)
{
    public const string CurrentSchemaVersion = "module-hir-state-payload-v2";

    public bool IsRestorable => Module != null &&
                                AttachedState.HasValidHash() &&
                                UnsupportedNodeCount == 0 &&
                                UnsupportedNodeKinds.Count == 0 &&
                                HasValidHash();

    public static ModuleHirStatePayload Create(HirModule? module, ParameterEffectMap? parameterEffects = null,
        IReadOnlySet<TypeId>? copyLikeTypeIds = null, IReadOnlyDictionary<TypeId, string>? dynamicTypeKeys = null,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors = null,
        IReadOnlyDictionary<int, List<ConstructorTypeLayout>>? constructorLayouts = null)
    {
        var attachedState = ModuleHirAttachedStatePayload.Create(
            parameterEffects,
            copyLikeTypeIds,
            dynamicTypeKeys,
            typeDescriptors,
            constructorLayouts);

        if (module == null)
        {
            var empty = new ModuleHirStatePayload(CurrentSchemaVersion, null, attachedState, 0, [], "");
            return empty with { Hash = ComputeHash(empty) };
        }

        var context = new HirStatePayloadCreateContext();
        var modulePayload = HirStateModulePayload.Create(module, context);
        var payload = new ModuleHirStatePayload(
            CurrentSchemaVersion,
            modulePayload,
            attachedState,
            context.UnsupportedNodeCount,
            context.UnsupportedNodeKinds.Order(StringComparer.Ordinal).ToArray(),
            "");

        return payload with { Hash = ComputeHash(payload) };
    }

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ComputeHash(this), StringComparison.Ordinal);

    public bool TryRestore(out HirModule module) => TryRestore(out module, out _);

    public bool TryRestore(out HirModule module, out ModuleHirAttachedState attachedState)
    {
        module = new HirModule();
        attachedState = ModuleHirAttachedState.Empty;
        if (SchemaVersion != CurrentSchemaVersion ||
            Module == null ||
            UnsupportedNodeCount != 0 ||
            UnsupportedNodeKinds.Count != 0 ||
            !HasValidHash() ||
            !AttachedState.TryRestore(out attachedState))
        {
            return false;
        }

        return Module.TryRestore(out module);
    }

    private static string ComputeHash(ModuleHirStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" });
}

public sealed record HirStateModulePayload(
    HirStateNodeHeader Header,
    string Name,
    string? PackageAlias,
    string? PackageInstanceKey,
    IReadOnlyList<string> Path,
    IReadOnlyList<int> Exports,
    IReadOnlyList<HirStateImportPayload> Imports,
    IReadOnlyList<string> LinkLibraries,
    IReadOnlyList<HirStateDeclPayload> Declarations)
{
    public static HirStateModulePayload Create(HirModule module, HirStatePayloadCreateContext context) =>
        new(
            HirStateNodeHeader.Create(module),
            module.Name,
            module.PackageAlias,
            module.PackageInstanceKey,
            module.Path.ToArray(),
            module.Exports.Select(static id => id.Value).ToArray(),
            module.Imports.Select(HirStateImportPayload.Create).ToArray(),
            module.LinkLibraries.ToArray(),
            module.Declarations
                .Select(declaration => HirStateDeclPayload.Create(declaration, context))
                .ToArray());

    public bool TryRestore(out HirModule module)
    {
        module = new HirModule
        {
            Span = Header.ToSourceSpan(),
            TypeId = new TypeId(Header.TypeId),
            SymbolId = new SymbolId(Header.SymbolId),
            Name = Name,
            IsModuleLevel = Header.IsModuleLevel,
            PackageAlias = PackageAlias,
            PackageInstanceKey = PackageInstanceKey,
            Path = Path.ToList(),
            Exports = Exports.Select(static id => new SymbolId(id)).ToList(),
            Imports = Imports.Select(static import => import.Restore()).ToList(),
            LinkLibraries = LinkLibraries.ToList()
        };

        foreach (var declarationPayload in Declarations)
        {
            if (!declarationPayload.TryRestore(out var declaration))
            {
                return false;
            }

            module.Declarations.Add(declaration);
        }

        return true;
    }
}

public sealed record HirStateImportPayload(
    IReadOnlyList<string> Path,
    string? Alias,
    IReadOnlyList<string> SelectiveImports,
    bool IsUse)
{
    public static HirStateImportPayload Create(HirImport import) =>
        new(import.Path.ToArray(), import.Alias, import.SelectiveImports.ToArray(), import.IsUse);

    public HirImport Restore() =>
        new()
        {
            Path = Path.ToList(),
            Alias = Alias,
            SelectiveImports = SelectiveImports.ToList(),
            IsUse = IsUse
        };
}

public sealed record HirStateDeclPayload(
    string Kind,
    HirStateNodeHeader Header,
    string Name,
    string SourceName,
    IReadOnlyList<HirStateTypeParamPayload> TypeParams,
    IReadOnlyList<HirStateParamPayload> Parameters,
    int ReturnType,
    HirStateNodePayload? Body,
    IReadOnlyList<int> RequiredAbilities,
    bool IsComptime,
    bool IsEntry,
    bool IsExternal,
    string? ExternalSymbolName,
    string? ExternalLibrary,
    string? IntrinsicName,
    string BuiltinIntrinsicRole,
    HirStatePatternPayload? Pattern,
    int TypeAnnotation,
    HirStateNodePayload? Initializer,
    IReadOnlyList<HirStateCtorPayload> Constructors,
    int AliasTarget,
    bool IsEnum,
    bool IsRecord,
    IReadOnlyList<HirStateAssocTypePayload> AssociatedTypes,
    IReadOnlyList<HirStateDeclPayload> Methods,
    IReadOnlyList<int> SuperTraits,
    int TraitId,
    int ImplementingType,
    HirStateImplPayload? ImplMetadata,
    int TargetType)
{
    public const string FuncKind = nameof(HirFunc);
    public const string ValKind = nameof(HirVal);
    public const string VarKind = nameof(HirVarDecl);
    public const string AdtKind = nameof(HirAdt);
    public const string EffectKind = nameof(HirEffect);
    public const string TraitKind = nameof(HirTrait);
    public const string ImplKind = nameof(HirImpl);
    public const string TypeAliasKind = nameof(HirTypeAlias);
    public const string UnsupportedKind = "Unsupported";

    public static HirStateDeclPayload Create(HirDecl declaration, HirStatePayloadCreateContext context)
    {
        return declaration switch
        {
            HirFunc func => Empty(FuncKind, func) with
            {
                SourceName = func.SourceName,
                TypeParams = func.TypeParams.Select(HirStateTypeParamPayload.Create).ToArray(),
                Parameters = func.Parameters.Select(HirStateParamPayload.Create).ToArray(),
                ReturnType = func.ReturnType.Value,
                Body = func.Body == null ? null : HirStateNodePayload.Create(func.Body, context),
                RequiredAbilities = func.RequiredAbilities.Select(static id => id.Value).ToArray(),
                IsComptime = func.IsComptime,
                IsEntry = func.IsEntry,
                IsExternal = func.IsExternal,
                ExternalSymbolName = func.ExternalSymbolName,
                ExternalLibrary = func.ExternalLibrary,
                IntrinsicName = func.IntrinsicName,
                BuiltinIntrinsicRole = func.BuiltinIntrinsicRole.ToString()
            },
            HirVal val => Empty(ValKind, val) with
            {
                Pattern = HirStatePatternPayload.Create(val.Pattern, context),
                TypeAnnotation = val.TypeAnnotation.Value,
                Initializer = HirStateNodePayload.Create(val.Initializer, context),
                IsComptime = val.IsComptime
            },
            HirVarDecl varDecl => Empty(VarKind, varDecl) with
            {
                Pattern = HirStatePatternPayload.Create(varDecl.Pattern, context),
                TypeAnnotation = varDecl.TypeAnnotation.Value,
                Initializer = HirStateNodePayload.Create(varDecl.Initializer, context)
            },
            HirAdt adt => Empty(AdtKind, adt) with
            {
                TypeParams = adt.TypeParams.Select(HirStateTypeParamPayload.Create).ToArray(),
                Constructors = adt.Constructors.Select(HirStateCtorPayload.Create).ToArray(),
                AliasTarget = adt.AliasTarget.Value,
                IsEnum = adt.IsEnum,
                IsRecord = adt.IsRecord
            },
            HirEffect effect => Empty(EffectKind, effect),
            HirTrait trait => Empty(TraitKind, trait) with
            {
                TypeParams = trait.TypeParams.Select(HirStateTypeParamPayload.Create).ToArray(),
                AssociatedTypes = trait.AssociatedTypes.Select(HirStateAssocTypePayload.Create).ToArray(),
                Methods = trait.Methods.Select(method => Create(method, context)).ToArray(),
                SuperTraits = trait.SuperTraits.Select(static id => id.Value).ToArray()
            },
            HirImpl impl => Empty(ImplKind, impl) with
            {
                TraitId = impl.TraitId.Value,
                ImplementingType = impl.ImplementingType.Value,
                Methods = impl.Methods.Select(method => Create(method, context)).ToArray(),
                ImplMetadata = HirStateImplPayload.Create(impl.ImplMetadata)
            },
            HirTypeAlias alias => Empty(TypeAliasKind, alias) with
            {
                TypeParams = alias.TypeParams.Select(HirStateTypeParamPayload.Create).ToArray(),
                TargetType = alias.TargetType.Value
            },
            _ => CreateUnsupported(declaration, context)
        };
    }

    public bool TryRestore(out HirDecl declaration)
    {
        declaration = new HirFunc();
        switch (Kind)
        {
            case FuncKind:
                if (!TryRestoreFunc(out var func))
                {
                    return false;
                }

                declaration = func;
                return true;
            case ValKind:
                if (Pattern == null || Initializer == null ||
                    !Pattern.TryRestore(out var valPattern) ||
                    !Initializer.TryRestore(out var valInitializer))
                {
                    return false;
                }

                declaration = ApplyDeclHeader(new HirVal
                {
                    Pattern = valPattern,
                    TypeAnnotation = new TypeId(TypeAnnotation),
                    Initializer = valInitializer,
                    IsComptime = IsComptime
                });
                return true;
            case VarKind:
                if (Pattern == null || Initializer == null ||
                    !Pattern.TryRestore(out var varPattern) ||
                    !Initializer.TryRestore(out var varInitializer))
                {
                    return false;
                }

                declaration = ApplyDeclHeader(new HirVarDecl
                {
                    Pattern = varPattern,
                    TypeAnnotation = new TypeId(TypeAnnotation),
                    Initializer = varInitializer
                });
                return true;
            case AdtKind:
                declaration = ApplyDeclHeader(new HirAdt
                {
                    TypeParams = TypeParams.Select(static parameter => parameter.Restore()).ToList(),
                    Constructors = Constructors.Select(static ctor => ctor.Restore()).ToList(),
                    AliasTarget = new TypeId(AliasTarget),
                    IsEnum = IsEnum,
                    IsRecord = IsRecord
                });
                return true;
            case EffectKind:
                declaration = ApplyDeclHeader(new HirEffect());
                return true;
            case TraitKind:
                if (!TryRestoreDeclList(Methods, out var methods))
                {
                    return false;
                }

                declaration = ApplyDeclHeader(new HirTrait
                {
                    TypeParams = TypeParams.Select(static parameter => parameter.Restore()).ToList(),
                    AssociatedTypes = AssociatedTypes.Select(static type => type.Restore()).ToList(),
                    Methods = methods.Cast<HirFunc>().ToList(),
                    SuperTraits = SuperTraits.Select(static id => new SymbolId(id)).ToList()
                });
                return true;
            case ImplKind:
                if (!TryRestoreDeclList(Methods, out var implMethods))
                {
                    return false;
                }

                declaration = ApplyDeclHeader(new HirImpl
                {
                    TraitId = new SymbolId(TraitId),
                    ImplementingType = new TypeId(ImplementingType),
                    Methods = implMethods.Cast<HirFunc>().ToList(),
                    ImplMetadata = ImplMetadata?.Restore()
                });
                return true;
            case TypeAliasKind:
                declaration = ApplyDeclHeader(new HirTypeAlias
                {
                    TypeParams = TypeParams.Select(static parameter => parameter.Restore()).ToList(),
                    TargetType = new TypeId(TargetType)
                });
                return true;
            default:
                return false;
        }
    }

    private bool TryRestoreFunc(out HirFunc func)
    {
        func = new HirFunc();
        HirNode? body = null;
        if (Body != null && !Body.TryRestore(out body))
        {
            return false;
        }

        if (!Enum.TryParse<Eidosc.Symbols.BuiltinIntrinsicRole>(BuiltinIntrinsicRole, out var builtinIntrinsicRole))
        {
            return false;
        }

        func = ApplyDeclHeader(new HirFunc
        {
            SourceName = SourceName,
            TypeParams = TypeParams.Select(static parameter => parameter.Restore()).ToList(),
            Parameters = Parameters.Select(static parameter => parameter.Restore()).ToList(),
            ReturnType = new TypeId(ReturnType),
            Body = body,
            RequiredAbilities = RequiredAbilities.Select(static id => new SymbolId(id)).ToList(),
            IsComptime = IsComptime,
            IsEntry = IsEntry,
            IsExternal = IsExternal,
            ExternalSymbolName = ExternalSymbolName,
            ExternalLibrary = ExternalLibrary,
            IntrinsicName = IntrinsicName,
            BuiltinIntrinsicRole = builtinIntrinsicRole
        });
        return true;
    }

    private T ApplyDeclHeader<T>(T declaration)
        where T : HirDecl =>
        declaration with
        {
            Span = Header.ToSourceSpan(),
            TypeId = new TypeId(Header.TypeId),
            SymbolId = new SymbolId(Header.SymbolId),
            Name = Name,
            IsModuleLevel = Header.IsModuleLevel
        };

    private static HirStateDeclPayload Empty(string kind, HirDecl declaration) =>
        new(
            kind,
            HirStateNodeHeader.Create(declaration),
            declaration.Name,
            "",
            [],
            [],
            TypeId.None.Value,
            null,
            [],
            false,
            false,
            false,
            null,
            null,
            null,
            Eidosc.Symbols.BuiltinIntrinsicRole.None.ToString(),
            null,
            TypeId.None.Value,
            null,
            [],
            TypeId.None.Value,
            false,
            false,
            [],
            [],
            [],
            SymbolId.None.Value,
            TypeId.None.Value,
            null,
            TypeId.None.Value);

    private static HirStateDeclPayload CreateUnsupported(HirDecl declaration, HirStatePayloadCreateContext context)
    {
        context.MarkUnsupported(declaration);
        return Empty(UnsupportedKind, declaration);
    }

    private static bool TryRestoreDeclList(
        IReadOnlyList<HirStateDeclPayload> payloads,
        out List<HirDecl> declarations)
    {
        declarations = [];
        foreach (var payload in payloads)
        {
            if (!payload.TryRestore(out var declaration))
            {
                return false;
            }

            declarations.Add(declaration);
        }

        return true;
    }
}

public sealed record HirStateNodePayload(
    string Kind,
    HirStateNodeHeader Header,
    string? Reason,
    bool IsRecovered,
    string LiteralKind,
    HirStateLiteralValuePayload? LiteralValue,
    string Name,
    IReadOnlyList<int> TypeArgumentIds,
    string Operator,
    HirStateNodePayload? Left,
    HirStateNodePayload? Right,
    HirStateNodePayload? Operand,
    HirStateNodePayload? Function,
    IReadOnlyList<HirStateNodePayload> Arguments,
    string Convention,
    string SurfaceSyntax,
    int OwnerSymbolId,
    string OwnerPath,
    bool HasExplicitOwner,
    int? ReceiverArgumentIndex,
    int InjectedArgumentCount,
    HirStateNodePayload? Condition,
    HirStateNodePayload? ThenBranch,
    HirStateNodePayload? ElseBranch,
    HirStateNodePayload? Value,
    HirStatePatternPayload? Pattern,
    HirStateNodePayload? SourceExpression,
    IReadOnlyList<HirStateNodePayload> Guards,
    HirStateNodePayload? Scrutinee,
    IReadOnlyList<HirStateMatchBranchPayload> Branches,
    bool IsExhaustive,
    IReadOnlyList<HirStateParamPayload> Parameters,
    int ReturnType,
    HirStateNodePayload? Body,
    IReadOnlyList<HirStateCapturePayload> Captures,
    IReadOnlyList<HirStateStatementPayload> Statements,
    HirStateNodePayload? Result,
    IReadOnlyList<HirStateNodePayload> Elements,
    bool HasRest,
    HirStateNodePayload? Output,
    IReadOnlyList<HirStateQualifierPayload> Qualifiers,
    HirStateNodePayload? Target,
    string FieldName,
    int FieldSymbolId,
    HirStateNodePayload? Index,
    string TargetKind)
{
    public const string ErrorKind = nameof(HirError);
    public const string LiteralKindName = nameof(HirLiteral);
    public const string VarKind = nameof(HirVar);
    public const string BinOpKind = nameof(HirBinOp);
    public const string UnaryOpKind = nameof(HirUnaryOp);
    public const string CallKind = nameof(HirCall);
    public const string IfKind = nameof(HirIf);
    public const string LoopKind = nameof(HirLoop);
    public const string BreakKind = nameof(HirBreak);
    public const string ReturnKind = nameof(HirReturn);
    public const string ContinueKind = nameof(HirContinue);
    public const string UnreachableKind = nameof(HirUnreachable);
    public const string PatternGuardKind = nameof(HirPatternGuard);
    public const string SequentialGuardKind = nameof(HirSequentialGuard);
    public const string MatchKind = nameof(HirMatch);
    public const string LambdaKind = nameof(HirLambda);
    public const string BlockKind = nameof(HirBlock);
    public const string TupleKind = nameof(HirTuple);
    public const string ListKind = nameof(HirList);
    public const string ListComprehensionKind = nameof(HirListComprehension);
    public const string FieldAccessKind = nameof(HirFieldAccess);
    public const string IndexAccessKind = nameof(HirIndexAccess);
    public const string UnsupportedKind = "Unsupported";

    public static HirStateNodePayload Create(HirNode node, HirStatePayloadCreateContext context)
    {
        return node switch
        {
            HirError error => Empty(ErrorKind, error) with
            {
                Reason = error.Reason,
                IsRecovered = error.IsRecovered
            },
            HirLiteral literal => Empty(LiteralKindName, literal) with
            {
                LiteralKind = literal.LiteralKind.ToString(),
                LiteralValue = HirStateLiteralValuePayload.Create(literal.Value)
            },
            HirVar variable => Empty(VarKind, variable) with
            {
                Name = variable.Name,
                TypeArgumentIds = variable.TypeArgumentIds.Select(static id => id.Value).ToArray()
            },
            HirBinOp binOp => Empty(BinOpKind, binOp) with
            {
                Operator = binOp.Operator.ToString(),
                Left = Create(binOp.Left, context),
                Right = Create(binOp.Right, context)
            },
            HirUnaryOp unaryOp => Empty(UnaryOpKind, unaryOp) with
            {
                Operator = unaryOp.Operator.ToString(),
                Operand = Create(unaryOp.Operand, context)
            },
            HirCall call => Empty(CallKind, call) with
            {
                Function = Create(call.Function, context),
                Arguments = call.Arguments.Select(argument => Create(argument, context)).ToArray(),
                Convention = call.Convention.ToString(),
                SurfaceSyntax = call.SurfaceSyntax.ToString(),
                OwnerSymbolId = call.OwnerSymbolId.Value,
                OwnerPath = call.OwnerPath,
                HasExplicitOwner = call.HasExplicitOwner,
                ReceiverArgumentIndex = call.ReceiverArgumentIndex,
                InjectedArgumentCount = call.InjectedArgumentCount
            },
            HirIf ifExpr => Empty(IfKind, ifExpr) with
            {
                Condition = Create(ifExpr.Condition, context),
                ThenBranch = Create(ifExpr.ThenBranch, context),
                ElseBranch = ifExpr.ElseBranch == null ? null : Create(ifExpr.ElseBranch, context)
            },
            HirLoop loop => Empty(LoopKind, loop) with
            {
                Body = Create(loop.Body, context)
            },
            HirBreak breakExpr => Empty(BreakKind, breakExpr) with
            {
                Value = breakExpr.Value == null ? null : Create(breakExpr.Value, context)
            },
            HirReturn returnExpr => Empty(ReturnKind, returnExpr) with
            {
                Value = returnExpr.Value == null ? null : Create(returnExpr.Value, context)
            },
            HirContinue continueExpr => Empty(ContinueKind, continueExpr),
            HirUnreachable unreachable => Empty(UnreachableKind, unreachable),
            HirPatternGuard guard => Empty(PatternGuardKind, guard) with
            {
                Pattern = HirStatePatternPayload.Create(guard.Pattern, context),
                SourceExpression = Create(guard.SourceExpression, context)
            },
            HirSequentialGuard sequentialGuard => Empty(SequentialGuardKind, sequentialGuard) with
            {
                Guards = sequentialGuard.Guards.Select(guard => Create(guard, context)).ToArray()
            },
            HirMatch match => Empty(MatchKind, match) with
            {
                Scrutinee = Create(match.Scrutinee, context),
                Branches = match.Branches.Select(branch => HirStateMatchBranchPayload.Create(branch, context)).ToArray(),
                IsExhaustive = match.IsExhaustive
            },
            HirLambda lambda => Empty(LambdaKind, lambda) with
            {
                Parameters = lambda.Parameters.Select(HirStateParamPayload.Create).ToArray(),
                ReturnType = lambda.ReturnType.Value,
                Body = Create(lambda.Body, context),
                Captures = lambda.Captures.Select(HirStateCapturePayload.Create).ToArray()
            },
            HirBlock block => Empty(BlockKind, block) with
            {
                Statements = block.Statements.Select(statement => HirStateStatementPayload.Create(statement, context)).ToArray(),
                Result = block.Result == null ? null : Create(block.Result, context)
            },
            HirTuple tuple => Empty(TupleKind, tuple) with
            {
                Elements = tuple.Elements.Select(element => Create(element, context)).ToArray()
            },
            HirList list => Empty(ListKind, list) with
            {
                Elements = list.Elements.Select(element => Create(element, context)).ToArray(),
                HasRest = list.HasRest
            },
            HirListComprehension comprehension => Empty(ListComprehensionKind, comprehension) with
            {
                Output = Create(comprehension.Output, context),
                Qualifiers = comprehension.Qualifiers.Select(qualifier => HirStateQualifierPayload.Create(qualifier, context)).ToArray()
            },
            HirFieldAccess fieldAccess => Empty(FieldAccessKind, fieldAccess) with
            {
                Target = Create(fieldAccess.Target, context),
                FieldName = fieldAccess.FieldName,
                FieldSymbolId = fieldAccess.FieldSymbolId.Value
            },
            HirIndexAccess indexAccess => Empty(IndexAccessKind, indexAccess) with
            {
                Target = Create(indexAccess.Target, context),
                Index = Create(indexAccess.Index, context),
                TargetKind = indexAccess.TargetKind.ToString()
            },
            _ => CreateUnsupported(node, context)
        };
    }

    public bool TryRestore(out HirNode node)
    {
        node = new HirError();
        switch (Kind)
        {
            case ErrorKind:
                node = ApplyHeader(new HirError { Reason = Reason ?? "", IsRecovered = IsRecovered });
                return true;
            case LiteralKindName:
                if (!Enum.TryParse<LiteralKind>(LiteralKind, out var literalKind) ||
                    LiteralValue == null ||
                    !LiteralValue.TryRestore(out var literalValue))
                {
                    return false;
                }

                node = ApplyHeader(new HirLiteral { LiteralKind = literalKind, Value = literalValue });
                return true;
            case VarKind:
                node = ApplyHeader(new HirVar
                {
                    Name = Name,
                    SymbolId = new SymbolId(Header.SymbolId),
                    TypeArgumentIds = TypeArgumentIds.Select(static id => new TypeId(id)).ToList()
                });
                return true;
            case BinOpKind:
                if (!Enum.TryParse<BinaryOp>(Operator, out var binaryOp) ||
                    Left == null ||
                    Right == null ||
                    !Left.TryRestore(out var left) ||
                    !Right.TryRestore(out var right))
                {
                    return false;
                }

                node = ApplyHeader(new HirBinOp { Operator = binaryOp, Left = left, Right = right });
                return true;
            case UnaryOpKind:
                if (!Enum.TryParse<UnaryOp>(Operator, out var unaryOp) ||
                    Operand == null ||
                    !Operand.TryRestore(out var operand))
                {
                    return false;
                }

                node = ApplyHeader(new HirUnaryOp { Operator = unaryOp, Operand = operand });
                return true;
            case CallKind:
                return TryRestoreCall(out node);
            case IfKind:
                return TryRestoreIf(out node);
            case LoopKind:
                if (Body == null || !Body.TryRestore(out var loopBody))
                {
                    return false;
                }

                node = ApplyHeader(new HirLoop { Body = loopBody });
                return true;
            case BreakKind:
                HirNode? breakValue = null;
                if (Value != null && !Value.TryRestore(out breakValue))
                {
                    return false;
                }

                node = ApplyHeader(new HirBreak { Value = breakValue });
                return true;
            case ReturnKind:
                HirNode? returnValue = null;
                if (Value != null && !Value.TryRestore(out returnValue))
                {
                    return false;
                }

                node = ApplyHeader(new HirReturn { Value = returnValue });
                return true;
            case ContinueKind:
                node = ApplyHeader(new HirContinue());
                return true;
            case UnreachableKind:
                node = ApplyHeader(new HirUnreachable());
                return true;
            case PatternGuardKind:
                if (Pattern == null ||
                    SourceExpression == null ||
                    !Pattern.TryRestore(out var guardPattern) ||
                    !SourceExpression.TryRestore(out var sourceExpression))
                {
                    return false;
                }

                node = ApplyHeader(new HirPatternGuard { Pattern = guardPattern, SourceExpression = sourceExpression });
                return true;
            case SequentialGuardKind:
                if (!TryRestoreNodeList(Guards, out var guards))
                {
                    return false;
                }

                var sequentialGuard = new HirSequentialGuard();
                sequentialGuard.Guards.AddRange(guards);
                node = ApplyHeader(sequentialGuard);
                return true;
            case MatchKind:
                return TryRestoreMatch(out node);
            case LambdaKind:
                if (Body == null || !Body.TryRestore(out var lambdaBody))
                {
                    return false;
                }

                node = ApplyHeader(new HirLambda
                {
                    Parameters = Parameters.Select(static parameter => parameter.Restore()).ToList(),
                    ReturnType = new TypeId(ReturnType),
                    Body = lambdaBody,
                    Captures = Captures.Select(static capture => capture.Restore()).ToList()
                });
                return true;
            case BlockKind:
                HirNode? result = null;
                if (!TryRestoreStatementList(Statements, out var statements) ||
                    (Result != null && !Result.TryRestore(out result)))
                {
                    return false;
                }

                node = ApplyHeader(new HirBlock { Statements = statements, Result = result });
                return true;
            case TupleKind:
                if (!TryRestoreNodeList(Elements, out var tupleElements))
                {
                    return false;
                }

                node = ApplyHeader(new HirTuple { Elements = tupleElements });
                return true;
            case ListKind:
                if (!TryRestoreNodeList(Elements, out var listElements))
                {
                    return false;
                }

                node = ApplyHeader(new HirList { Elements = listElements, HasRest = HasRest });
                return true;
            case ListComprehensionKind:
                if (Output == null || !Output.TryRestore(out var output))
                {
                    return false;
                }

                node = ApplyHeader(new HirListComprehension
                {
                    Output = output,
                    Qualifiers = Qualifiers.Select(static qualifier => qualifier.Restore()).ToList()
                });
                return true;
            case FieldAccessKind:
                if (Target == null || !Target.TryRestore(out var fieldTarget))
                {
                    return false;
                }

                node = ApplyHeader(new HirFieldAccess
                {
                    Target = fieldTarget,
                    FieldName = FieldName,
                    FieldSymbolId = new SymbolId(FieldSymbolId)
                });
                return true;
            case IndexAccessKind:
                if (Target == null ||
                    Index == null ||
                    !Enum.TryParse<HirIndexAccessKind>(TargetKind, out var targetKind) ||
                    !Target.TryRestore(out var indexTarget) ||
                    !Index.TryRestore(out var index))
                {
                    return false;
                }

                node = ApplyHeader(new HirIndexAccess { Target = indexTarget, Index = index, TargetKind = targetKind });
                return true;
            default:
                return false;
        }
    }

    private bool TryRestoreCall(out HirNode node)
    {
        node = new HirError();
        if (Function == null ||
            !Enum.TryParse<CallConvention>(Convention, out var convention) ||
            !Enum.TryParse<HirCallSurfaceSyntax>(SurfaceSyntax, out var surfaceSyntax) ||
            !Function.TryRestore(out var function) ||
            !TryRestoreNodeList(Arguments, out var arguments))
        {
            return false;
        }

        node = ApplyHeader(new HirCall
        {
            Function = function,
            Arguments = arguments,
            Convention = convention,
            SurfaceSyntax = surfaceSyntax,
            OwnerSymbolId = new SymbolId(OwnerSymbolId),
            OwnerPath = OwnerPath,
            HasExplicitOwner = HasExplicitOwner,
            ReceiverArgumentIndex = ReceiverArgumentIndex,
            InjectedArgumentCount = InjectedArgumentCount
        });
        return true;
    }

    private bool TryRestoreIf(out HirNode node)
    {
        node = new HirError();
        HirNode? elseBranch = null;
        if (Condition == null ||
            ThenBranch == null ||
            !Condition.TryRestore(out var condition) ||
            !ThenBranch.TryRestore(out var thenBranch) ||
            (ElseBranch != null && !ElseBranch.TryRestore(out elseBranch)))
        {
            return false;
        }

        node = ApplyHeader(new HirIf { Condition = condition, ThenBranch = thenBranch, ElseBranch = elseBranch });
        return true;
    }

    private bool TryRestoreMatch(out HirNode node)
    {
        node = new HirError();
        if (Scrutinee == null || !Scrutinee.TryRestore(out var scrutinee))
        {
            return false;
        }

        var branches = new List<HirMatchBranch>(Branches.Count);
        foreach (var branchPayload in Branches)
        {
            if (!branchPayload.TryRestore(out var branch))
            {
                return false;
            }

            branches.Add(branch);
        }

        node = ApplyHeader(new HirMatch { Scrutinee = scrutinee, Branches = branches, IsExhaustive = IsExhaustive });
        return true;
    }

    private HirNode ApplyHeader(HirNode node) =>
        node with
        {
            Span = Header.ToSourceSpan(),
            TypeId = new TypeId(Header.TypeId),
            SymbolId = new SymbolId(Header.SymbolId)
        };

    private static HirStateNodePayload Empty(string kind, HirNode node) =>
        new(
            kind,
            HirStateNodeHeader.Create(node),
            null,
            false,
            "",
            null,
            "",
            [],
            "",
            null,
            null,
            null,
            null,
            [],
            CallConvention.Normal.ToString(),
            HirCallSurfaceSyntax.Direct.ToString(),
            SymbolId.None.Value,
            "",
            false,
            null,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            null,
            [],
            false,
            [],
            TypeId.None.Value,
            null,
            [],
            [],
            null,
            [],
            false,
            null,
            [],
            null,
            "",
            SymbolId.None.Value,
            null,
            HirIndexAccessKind.Unknown.ToString());

    private static HirStateNodePayload CreateUnsupported(HirNode node, HirStatePayloadCreateContext context)
    {
        context.MarkUnsupported(node);
        return Empty(UnsupportedKind, node);
    }

    private static bool TryRestoreNodeList(
        IReadOnlyList<HirStateNodePayload> payloads,
        out List<HirNode> nodes)
    {
        nodes = [];
        foreach (var payload in payloads)
        {
            if (!payload.TryRestore(out var node))
            {
                return false;
            }

            nodes.Add(node);
        }

        return true;
    }

    private static bool TryRestoreStatementList(
        IReadOnlyList<HirStateStatementPayload> payloads,
        out List<HirStatement> statements)
    {
        statements = [];
        foreach (var payload in payloads)
        {
            if (!payload.TryRestore(out var statement))
            {
                return false;
            }

            statements.Add(statement);
        }

        return true;
    }
}

public sealed record HirStatePatternPayload(
    string Kind,
    HirStatePatternHeader Header,
    string? Reason,
    bool IsRecovered,
    string Name,
    int SymbolId,
    bool IsWildcard,
    string BindingMode,
    bool IsMutableBinding,
    HirStateLiteralValuePayload? LiteralValue,
    string ConstructorName,
    int ConstructorSymbolId,
    IReadOnlyList<HirStateFieldPatternPayload> Fields,
    IReadOnlyList<HirStatePatternPayload> Elements,
    bool HasRest,
    HirStatePatternPayload? RestPattern,
    IReadOnlyList<HirStatePatternPayload> SuffixElements,
    HirStatePatternPayload? Left,
    HirStatePatternPayload? Right,
    HirStatePatternPayload? InnerPattern,
    HirStatePatternPayload? Start,
    HirStatePatternPayload? End,
    HirStateNodePayload? View,
    int ViewResultTypeId)
{
    public const string ErrorKind = nameof(HirErrorPattern);
    public const string VarKind = nameof(HirVarPattern);
    public const string LiteralKind = nameof(HirLiteralPattern);
    public const string CtorKind = nameof(HirCtorPattern);
    public const string TupleKind = nameof(HirTuplePattern);
    public const string ListKind = nameof(HirListPattern);
    public const string OrKind = nameof(HirOrPattern);
    public const string AndKind = nameof(HirAndPattern);
    public const string NotKind = nameof(HirNotPattern);
    public const string RangeKind = nameof(HirRangePattern);
    public const string ViewKind = nameof(HirViewPattern);
    public const string AsKind = nameof(HirAsPattern);
    public const string UnsupportedKind = "Unsupported";

    public static HirStatePatternPayload Create(HirPattern pattern, HirStatePayloadCreateContext context)
    {
        return pattern switch
        {
            HirErrorPattern error => Empty(ErrorKind, error) with
            {
                Reason = error.Reason,
                IsRecovered = error.IsRecovered
            },
            HirVarPattern varPattern => Empty(VarKind, varPattern) with
            {
                Name = varPattern.Name,
                SymbolId = varPattern.SymbolId.Value,
                IsWildcard = varPattern.IsWildcard,
                BindingMode = varPattern.BindingMode.ToString(),
                IsMutableBinding = varPattern.IsMutableBinding
            },
            HirLiteralPattern literal => Empty(LiteralKind, literal) with
            {
                LiteralValue = HirStateLiteralValuePayload.Create(literal.Value)
            },
            HirCtorPattern ctor => Empty(CtorKind, ctor) with
            {
                ConstructorName = ctor.ConstructorName,
                ConstructorSymbolId = ctor.ConstructorSymbolId.Value,
                Fields = ctor.Fields.Select(field => HirStateFieldPatternPayload.Create(field, context)).ToArray()
            },
            HirTuplePattern tuple => Empty(TupleKind, tuple) with
            {
                Elements = tuple.Elements.Select(element => Create(element, context)).ToArray()
            },
            HirListPattern list => Empty(ListKind, list) with
            {
                Elements = list.Elements.Select(element => Create(element, context)).ToArray(),
                HasRest = list.HasRest,
                RestPattern = list.RestPattern == null ? null : Create(list.RestPattern, context),
                SuffixElements = list.SuffixElements.Select(element => Create(element, context)).ToArray()
            },
            HirOrPattern orPattern => Empty(OrKind, orPattern) with
            {
                Left = Create(orPattern.Left, context),
                Right = Create(orPattern.Right, context)
            },
            HirAndPattern andPattern => Empty(AndKind, andPattern) with
            {
                Left = Create(andPattern.Left, context),
                Right = Create(andPattern.Right, context)
            },
            HirNotPattern notPattern => Empty(NotKind, notPattern) with
            {
                InnerPattern = Create(notPattern.InnerPattern, context)
            },
            HirRangePattern range => Empty(RangeKind, range) with
            {
                Start = Create(range.Start, context),
                End = Create(range.End, context)
            },
            HirViewPattern view => Empty(ViewKind, view) with
            {
                View = HirStateNodePayload.Create(view.View, context),
                ViewResultTypeId = view.ViewResultTypeId.Value,
                InnerPattern = Create(view.InnerPattern, context)
            },
            HirAsPattern asPattern => Empty(AsKind, asPattern) with
            {
                InnerPattern = Create(asPattern.InnerPattern, context),
                Name = asPattern.Name,
                SymbolId = asPattern.SymbolId.Value,
                BindingMode = asPattern.BindingMode.ToString(),
                IsMutableBinding = asPattern.IsMutableBinding
            },
            _ => CreateUnsupported(pattern, context)
        };
    }

    public bool TryRestore(out HirPattern pattern)
    {
        pattern = new HirErrorPattern();
        switch (Kind)
        {
            case ErrorKind:
                pattern = ApplyHeader(new HirErrorPattern { Reason = Reason ?? "", IsRecovered = IsRecovered });
                return true;
            case VarKind:
                if (!Enum.TryParse<PatternBindingMode>(BindingMode, out var bindingMode))
                {
                    return false;
                }

                pattern = ApplyHeader(new HirVarPattern
                {
                    Name = Name,
                    SymbolId = new SymbolId(SymbolId),
                    IsWildcard = IsWildcard,
                    BindingMode = bindingMode,
                    IsMutableBinding = IsMutableBinding
                });
                return true;
            case LiteralKind:
                if (LiteralValue == null || !LiteralValue.TryRestore(out var literalValue))
                {
                    return false;
                }

                pattern = ApplyHeader(new HirLiteralPattern { Value = literalValue });
                return true;
            case CtorKind:
                pattern = ApplyHeader(new HirCtorPattern
                {
                    ConstructorName = ConstructorName,
                    ConstructorSymbolId = new SymbolId(ConstructorSymbolId),
                    Fields = Fields.Select(static field => field.Restore()).ToList()
                });
                return true;
            case TupleKind:
                if (!TryRestorePatternList(Elements, out var tupleElements))
                {
                    return false;
                }

                pattern = ApplyHeader(new HirTuplePattern { Elements = tupleElements });
                return true;
            case ListKind:
                HirPattern? restPattern = null;
                if (!TryRestorePatternList(Elements, out var listElements) ||
                    !TryRestorePatternList(SuffixElements, out var suffixElements) ||
                    (RestPattern != null && !RestPattern.TryRestore(out restPattern)))
                {
                    return false;
                }

                pattern = ApplyHeader(new HirListPattern
                {
                    Elements = listElements,
                    HasRest = HasRest,
                    RestPattern = restPattern,
                    SuffixElements = suffixElements
                });
                return true;
            case OrKind:
                return TryRestoreBinaryPattern(static (left, right) => new HirOrPattern { Left = left, Right = right }, out pattern);
            case AndKind:
                return TryRestoreBinaryPattern(static (left, right) => new HirAndPattern { Left = left, Right = right }, out pattern);
            case NotKind:
                if (InnerPattern == null || !InnerPattern.TryRestore(out var innerNotPattern))
                {
                    return false;
                }

                pattern = ApplyHeader(new HirNotPattern { InnerPattern = innerNotPattern });
                return true;
            case RangeKind:
                if (Start == null ||
                    End == null ||
                    !Start.TryRestore(out var startPattern) ||
                    !End.TryRestore(out var endPattern) ||
                    startPattern is not HirLiteralPattern start ||
                    endPattern is not HirLiteralPattern end)
                {
                    return false;
                }

                pattern = ApplyHeader(new HirRangePattern { Start = start, End = end });
                return true;
            case ViewKind:
                if (View == null ||
                    InnerPattern == null ||
                    !View.TryRestore(out var view) ||
                    !InnerPattern.TryRestore(out var viewInnerPattern))
                {
                    return false;
                }

                pattern = ApplyHeader(new HirViewPattern
                {
                    View = view,
                    ViewResultTypeId = new TypeId(ViewResultTypeId),
                    InnerPattern = viewInnerPattern
                });
                return true;
            case AsKind:
                if (InnerPattern == null ||
                    !Enum.TryParse<PatternBindingMode>(BindingMode, out var asBindingMode) ||
                    !InnerPattern.TryRestore(out var asInnerPattern))
                {
                    return false;
                }

                pattern = ApplyHeader(new HirAsPattern
                {
                    InnerPattern = asInnerPattern,
                    Name = Name,
                    SymbolId = new SymbolId(SymbolId),
                    BindingMode = asBindingMode,
                    IsMutableBinding = IsMutableBinding
                });
                return true;
            default:
                return false;
        }
    }

    private HirPattern ApplyHeader(HirPattern pattern) =>
        pattern with
        {
            Span = Header.ToSourceSpan(),
            TypeId = new TypeId(Header.TypeId)
        };

    private bool TryRestoreBinaryPattern(
        Func<HirPattern, HirPattern, HirPattern> create,
        out HirPattern pattern)
    {
        pattern = new HirErrorPattern();
        if (Left == null ||
            Right == null ||
            !Left.TryRestore(out var leftPattern) ||
            !Right.TryRestore(out var rightPattern))
        {
            return false;
        }

        pattern = ApplyHeader(create(leftPattern, rightPattern));
        return true;
    }

    private static HirStatePatternPayload Empty(string kind, HirPattern pattern) =>
        new(
            kind,
            HirStatePatternHeader.Create(pattern),
            null,
            false,
            "",
            Eidosc.SymbolId.None.Value,
            false,
            PatternBindingMode.ByValue.ToString(),
            false,
            null,
            "",
            Eidosc.SymbolId.None.Value,
            [],
            [],
            false,
            null,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            TypeId.None.Value);

    private static HirStatePatternPayload CreateUnsupported(HirPattern pattern, HirStatePayloadCreateContext context)
    {
        context.MarkUnsupported(pattern);
        return Empty(UnsupportedKind, pattern);
    }

    private static bool TryRestorePatternList(
        IReadOnlyList<HirStatePatternPayload> payloads,
        out List<HirPattern> patterns)
    {
        patterns = [];
        foreach (var payload in payloads)
        {
            if (!payload.TryRestore(out var pattern))
            {
                return false;
            }

            patterns.Add(pattern);
        }

        return true;
    }
}

public sealed record HirStateStatementPayload(
    string Kind,
    SourceSpanPayload Span,
    HirStateDeclPayload? Declaration,
    HirStateNodePayload? Expression,
    HirStateNodePayload? Target,
    HirStateNodePayload? Value)
{
    public const string DeclKind = nameof(HirDeclStatement);
    public const string ExprKind = nameof(HirExprStatement);
    public const string AssignKind = nameof(HirAssignStatement);
    public const string UnsupportedKind = "Unsupported";

    public static HirStateStatementPayload Create(HirStatement statement, HirStatePayloadCreateContext context)
    {
        return statement switch
        {
            HirDeclStatement decl => new(
                DeclKind,
                SourceSpanPayload.Create(decl.Span),
                HirStateDeclPayload.Create(decl.Declaration, context),
                null,
                null,
                null),
            HirExprStatement expr => new(
                ExprKind,
                SourceSpanPayload.Create(expr.Span),
                null,
                HirStateNodePayload.Create(expr.Expression, context),
                null,
                null),
            HirAssignStatement assign => new(
                AssignKind,
                SourceSpanPayload.Create(assign.Span),
                null,
                null,
                HirStateNodePayload.Create(assign.Target, context),
                HirStateNodePayload.Create(assign.Value, context)),
            _ => CreateUnsupported(statement, context)
        };
    }

    public bool TryRestore(out HirStatement statement)
    {
        statement = new HirExprStatement();
        switch (Kind)
        {
            case DeclKind:
                if (Declaration == null || !Declaration.TryRestore(out var declaration))
                {
                    return false;
                }

                statement = new HirDeclStatement
                {
                    Span = Span.ToSourceSpan(),
                    Declaration = declaration
                };
                return true;
            case ExprKind:
                if (Expression == null || !Expression.TryRestore(out var expression))
                {
                    return false;
                }

                statement = new HirExprStatement
                {
                    Span = Span.ToSourceSpan(),
                    Expression = expression
                };
                return true;
            case AssignKind:
                if (Target == null ||
                    Value == null ||
                    !Target.TryRestore(out var target) ||
                    !Value.TryRestore(out var value))
                {
                    return false;
                }

                statement = new HirAssignStatement
                {
                    Span = Span.ToSourceSpan(),
                    Target = target,
                    Value = value
                };
                return true;
            default:
                return false;
        }
    }

    private static HirStateStatementPayload CreateUnsupported(HirStatement statement, HirStatePayloadCreateContext context)
    {
        context.MarkUnsupported(statement);
        return new HirStateStatementPayload(
            UnsupportedKind,
            SourceSpanPayload.Create(statement.Span),
            null,
            null,
            null,
            null);
    }
}

public sealed record HirStateNodeHeader(
    SourceSpanPayload Span,
    int TypeId,
    int SymbolId,
    bool IsModuleLevel)
{
    public static HirStateNodeHeader Create(HirNode node) =>
        new(
            SourceSpanPayload.Create(node.Span),
            node.TypeId.Value,
            node.SymbolId.Value,
            node is HirDecl declaration && declaration.IsModuleLevel);

    public SourceSpan ToSourceSpan() => Span.ToSourceSpan();
}

public sealed record HirStatePatternHeader(SourceSpanPayload Span, int TypeId)
{
    public static HirStatePatternHeader Create(HirPattern pattern) =>
        new(SourceSpanPayload.Create(pattern.Span), pattern.TypeId.Value);

    public SourceSpan ToSourceSpan() => Span.ToSourceSpan();
}

public sealed record HirStateTypeParamPayload(
    string Name,
    int SymbolId,
    int TypeId,
    string KindAnnotation,
    bool IsComptime,
    string? ComptimeTypeAnnotation,
    IReadOnlyList<HirStateTraitConstraintPayload> Constraints)
{
    public static HirStateTypeParamPayload Create(HirTypeParam typeParam) =>
        new(
            typeParam.Name,
            typeParam.SymbolId.Value,
            typeParam.TypeId.Value,
            typeParam.KindAnnotation,
            typeParam.IsComptime,
            typeParam.ComptimeTypeAnnotation,
            typeParam.Constraints.Select(HirStateTraitConstraintPayload.Create).ToArray());

    public HirTypeParam Restore() =>
        new()
        {
            Name = Name,
            SymbolId = new SymbolId(SymbolId),
            TypeId = new TypeId(TypeId),
            KindAnnotation = KindAnnotation,
            IsComptime = IsComptime,
            ComptimeTypeAnnotation = ComptimeTypeAnnotation,
            Constraints = Constraints.Select(static constraint => constraint.Restore()).ToList()
        };
}

public sealed record HirStateTraitConstraintPayload(
    int SymbolId,
    string Name,
    IReadOnlyList<string> ModulePath,
    IReadOnlyList<HirStateTypeArgPayload> TypeArgs)
{
    public static HirStateTraitConstraintPayload Create(HirTraitConstraint constraint) =>
        new(
            constraint.SymbolId.Value,
            constraint.Name,
            constraint.ModulePath.ToArray(),
            constraint.TypeArgs.Select(HirStateTypeArgPayload.Create).ToArray());

    public HirTraitConstraint Restore() =>
        new()
        {
            SymbolId = new SymbolId(SymbolId),
            Name = Name,
            ModulePath = ModulePath.ToList(),
            TypeArgs = TypeArgs.Select(static typeArg => typeArg.Restore()).ToList()
        };
}

public sealed record HirStateTypeArgPayload(int TypeId, string DisplayText)
{
    public static HirStateTypeArgPayload Create(HirTypeArg typeArg) =>
        new(typeArg.TypeId.Value, typeArg.DisplayText);

    public HirTypeArg Restore() =>
        new()
        {
            TypeId = new TypeId(TypeId),
            DisplayText = DisplayText
        };
}

public sealed record HirStateParamPayload(
    string Name,
    int TypeId,
    int SymbolId,
    bool IsMutable)
{
    public static HirStateParamPayload Create(HirParam param) =>
        new(param.Name, param.TypeId.Value, param.SymbolId.Value, param.IsMutable);

    public HirParam Restore() =>
        new()
        {
            Name = Name,
            TypeId = new TypeId(TypeId),
            SymbolId = new SymbolId(SymbolId),
            IsMutable = IsMutable
        };
}

public sealed record HirStateCapturePayload(
    string Name,
    int SymbolId,
    int TypeId,
    bool IsMutable)
{
    public static HirStateCapturePayload Create(HirCapture capture) =>
        new(capture.Name, capture.SymbolId.Value, capture.TypeId.Value, capture.IsMutable);

    public HirCapture Restore() =>
        new()
        {
            Name = Name,
            SymbolId = new SymbolId(SymbolId),
            TypeId = new TypeId(TypeId),
            IsMutable = IsMutable
        };
}

public sealed record HirStateCtorPayload(
    string Name,
    int SymbolId,
    SourceSpanPayload Span,
    IReadOnlyList<HirStateFieldPayload> Fields)
{
    public static HirStateCtorPayload Create(HirCtor ctor) =>
        new(
            ctor.Name,
            ctor.SymbolId.Value,
            SourceSpanPayload.Create(ctor.Span),
            ctor.Fields.Select(HirStateFieldPayload.Create).ToArray());

    public HirCtor Restore() =>
        new()
        {
            Name = Name,
            SymbolId = new SymbolId(SymbolId),
            Span = Span.ToSourceSpan(),
            Fields = Fields.Select(static field => field.Restore()).ToList()
        };
}

public sealed record HirStateFieldPayload(string? Name, int TypeId, int SymbolId)
{
    public static HirStateFieldPayload Create(HirField field) =>
        new(field.Name, field.TypeId.Value, field.SymbolId.Value);

    public HirField Restore() =>
        new()
        {
            Name = Name,
            TypeId = new TypeId(TypeId),
            SymbolId = new SymbolId(SymbolId)
        };
}

public sealed record HirStateAssocTypePayload(string Name, int SymbolId, int DefaultType)
{
    public static HirStateAssocTypePayload Create(HirAssocType assocType) =>
        new(assocType.Name, assocType.SymbolId.Value, assocType.DefaultType.Value);

    public HirAssocType Restore() =>
        new()
        {
            Name = Name,
            SymbolId = new SymbolId(SymbolId),
            DefaultType = new TypeId(DefaultType)
        };
}

public sealed record HirStateImplPayload(
    int Id,
    string Name,
    int TraitId,
    int ImplementingTypeId,
    SourceSpanPayload Span,
    bool IsTypeResolved,
    bool IsModuleLevel,
    bool IsPublic,
    int TypeId,
    IReadOnlyList<int> Methods)
{
    public static HirStateImplPayload? Create(ImplSymbol? impl)
    {
        if (impl == null)
        {
            return null;
        }

        return new HirStateImplPayload(
            impl.Id.Value,
            impl.Name,
            impl.Trait.Value,
            impl.ImplementingType.Value,
            SourceSpanPayload.Create(impl.Span),
            impl.IsTypeResolved,
            impl.IsModuleLevel,
            impl.IsPublic,
            impl.TypeId.Value,
            impl.Methods.Select(static id => id.Value).ToArray());
    }

    public ImplSymbol Restore()
    {
        var symbol = new ImplSymbol
        {
            Id = new SymbolId(Id),
            Name = Name,
            Trait = new SymbolId(TraitId),
            ImplementingType = new TypeId(ImplementingTypeId),
            Span = Span.ToSourceSpan(),
            IsTypeResolved = IsTypeResolved,
            IsModuleLevel = IsModuleLevel,
            IsPublic = IsPublic,
            TypeId = new TypeId(TypeId)
        };
        symbol.Methods.AddRange(Methods.Select(static id => new SymbolId(id)));
        return symbol;
    }
}

public sealed record HirStateMatchBranchPayload(
    HirStatePatternPayload Pattern,
    HirStateNodePayload? Guard,
    HirStateNodePayload Body)
{
    public static HirStateMatchBranchPayload Create(HirMatchBranch branch, HirStatePayloadCreateContext context) =>
        new(
            HirStatePatternPayload.Create(branch.Pattern, context),
            branch.Guard == null ? null : HirStateNodePayload.Create(branch.Guard, context),
            HirStateNodePayload.Create(branch.Body, context));

    public bool TryRestore(out HirMatchBranch branch)
    {
        branch = new HirMatchBranch();
        HirNode? guard = null;
        if (!Pattern.TryRestore(out var pattern) ||
            (Guard != null && !Guard.TryRestore(out guard)) ||
            !Body.TryRestore(out var body))
        {
            return false;
        }

        branch = new HirMatchBranch
        {
            Pattern = pattern,
            Guard = guard,
            Body = body
        };
        return true;
    }
}

public sealed record HirStateQualifierPayload(
    string Kind,
    HirStatePatternPayload? GeneratorPattern,
    HirStateNodePayload? GeneratorSource,
    HirStateNodePayload? GuardExpression,
    SourceSpanPayload Span)
{
    public static HirStateQualifierPayload Create(HirQualifier qualifier, HirStatePayloadCreateContext context) =>
        new(
            qualifier.Kind.ToString(),
            qualifier.GeneratorPattern == null ? null : HirStatePatternPayload.Create(qualifier.GeneratorPattern, context),
            qualifier.GeneratorSource == null ? null : HirStateNodePayload.Create(qualifier.GeneratorSource, context),
            qualifier.GuardExpression == null ? null : HirStateNodePayload.Create(qualifier.GuardExpression, context),
            SourceSpanPayload.Create(qualifier.Span));

    public HirQualifier Restore()
    {
        _ = Enum.TryParse<HirQualifierKind>(Kind, out var kind);
        HirPattern? generatorPattern = null;
        HirNode? generatorSource = null;
        HirNode? guardExpression = null;
        _ = GeneratorPattern?.TryRestore(out generatorPattern);
        _ = GeneratorSource?.TryRestore(out generatorSource);
        _ = GuardExpression?.TryRestore(out guardExpression);
        return new HirQualifier
        {
            Kind = kind,
            GeneratorPattern = generatorPattern,
            GeneratorSource = generatorSource,
            GuardExpression = guardExpression,
            Span = Span.ToSourceSpan()
        };
    }
}

public sealed record HirStateFieldPatternPayload(string FieldName, HirStatePatternPayload Pattern)
{
    public static HirStateFieldPatternPayload Create(HirFieldPattern field, HirStatePayloadCreateContext context) =>
        new(field.FieldName, HirStatePatternPayload.Create(field.Pattern, context));

    public HirFieldPattern Restore()
    {
        _ = Pattern.TryRestore(out var pattern);
        return new HirFieldPattern
        {
            FieldName = FieldName,
            Pattern = pattern
        };
    }
}

public sealed record HirStateLiteralValuePayload(string Kind, string? Value)
{
    public const string NullKind = "Null";
    public const string BoolKind = "Bool";
    public const string CharKind = "Char";
    public const string StringKind = "String";
    public const string IntKind = "Int";
    public const string LongKind = "Long";
    public const string FloatKind = "Float";
    public const string DoubleKind = "Double";

    public static HirStateLiteralValuePayload Create(object? value) =>
        value switch
        {
            null => new HirStateLiteralValuePayload(NullKind, null),
            bool scalar => new HirStateLiteralValuePayload(BoolKind, scalar ? "true" : "false"),
            char scalar => new HirStateLiteralValuePayload(CharKind, scalar.ToString()),
            string scalar => new HirStateLiteralValuePayload(StringKind, scalar),
            int scalar => new HirStateLiteralValuePayload(IntKind, scalar.ToString(CultureInfo.InvariantCulture)),
            long scalar => new HirStateLiteralValuePayload(LongKind, scalar.ToString(CultureInfo.InvariantCulture)),
            float scalar => new HirStateLiteralValuePayload(FloatKind, scalar.ToString("R", CultureInfo.InvariantCulture)),
            double scalar => new HirStateLiteralValuePayload(DoubleKind, scalar.ToString("R", CultureInfo.InvariantCulture)),
            _ => new HirStateLiteralValuePayload(StringKind, value.ToString())
        };

    public bool TryRestore(out object? value)
    {
        switch (Kind)
        {
            case NullKind:
                value = null;
                return true;
            case BoolKind when bool.TryParse(Value, out var boolValue):
                value = boolValue;
                return true;
            case CharKind when Value is { Length: > 0 }:
                value = Value[0];
                return true;
            case StringKind:
                value = Value ?? "";
                return true;
            case IntKind when int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue):
                value = intValue;
                return true;
            case LongKind when long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue):
                value = longValue;
                return true;
            case FloatKind when float.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue):
                value = floatValue;
                return true;
            case DoubleKind when double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue):
                value = doubleValue;
                return true;
            default:
                value = null;
                return false;
        }
    }
}

public sealed class HirStatePayloadCreateContext
{
    public int UnsupportedNodeCount { get; private set; }

    public SortedSet<string> UnsupportedNodeKinds { get; } = new(StringComparer.Ordinal);

    public void MarkUnsupported(object node)
    {
        UnsupportedNodeCount++;
        UnsupportedNodeKinds.Add(node.GetType().Name);
    }
}

internal static class SourceSpanPayloadExtensions
{
    public static SourceSpan ToSourceSpan(this SourceSpanPayload payload) =>
        new(new SourceLocation(payload.Position, payload.Line, payload.Column, payload.FilePath), payload.Length);
}
