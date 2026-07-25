using Eidosc.Symbols;
using System.Collections.Frozen;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Mir.Closure;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using DrivenType = Eidosc.Types.Type;

namespace Eidosc.Hir;

// Binary expression conversion, stdlib calls, pipe operators
public sealed partial class HirBuilder
{


    private HirNode ConvertIndex(IndexExpr indexExpr)
    {
        if (indexExpr.IsTypeApplication)
        {
            var target = ConvertExprOrFallback(indexExpr.Object, "type application target", indexExpr.Span);
            return AttachExplicitTypeArguments(target, indexExpr.TypeArgs, indexExpr);
        }

        // 反糖化后的 IndexExpr：TypeArgs 已清空，Index 为 null，直接透传 Object
        if (indexExpr.Index == null)
        {
            return ConvertExprOrFallback(indexExpr.Object, "index target", indexExpr.Span);
        }

        return new HirIndexAccess
        {
            Target = ConvertExprOrFallback(indexExpr.Object, "index target", indexExpr.Span),
            Index = ConvertExprOrFallback(indexExpr.Index, "index expression", indexExpr.Span),
            TargetKind = ResolveIndexTargetKind(indexExpr.Object),
            Span = indexExpr.Span,
            TypeId = GetTypeId(indexExpr)
        };
    }

    private HirIndexAccessKind ResolveIndexTargetKind(EidosAstNode? target)
    {
        if (target == null)
        {
            return HirIndexAccessKind.Unknown;
        }

        if (TryGetResolvedInferredType(target, out var inferredType))
        {
            var resolved = ResolveIndexTargetKindFromType(inferredType);
            if (resolved != HirIndexAccessKind.Unknown)
            {
                return resolved;
            }
        }

        return target switch
        {
            ListExpr or ListComprehension => HirIndexAccessKind.RuntimeArray,
            TupleExpr => HirIndexAccessKind.Aggregate,
            _ => HirIndexAccessKind.Unknown
        };
    }

    private static HirIndexAccessKind ResolveIndexTargetKindFromType(DrivenType type)
    {
        var normalized = NormalizeType(type);
        return normalized switch
        {
            TyCon { Name: WellKnownStrings.BuiltinTypes.Seq } => HirIndexAccessKind.RuntimeArray,
            TyTuple => HirIndexAccessKind.Aggregate,
            _ => HirIndexAccessKind.Unknown
        };
    }

    private static DrivenType NormalizeType(DrivenType type)
    {
        while (true)
        {
            switch (type)
            {
                case TyVar { Instance: not null } tyVar:
                    type = tyVar.Instance!;
                    continue;

                case TyRef reference:
                    type = reference.Inner;
                    continue;

                case TyMutRef mutReference:
                    type = mutReference.Inner;
                    continue;

                default:
                    return type;
            }
        }
    }


    private HirNode ReportAndCreateError(
        string message,
        SourceSpan span,
        string code,
        string fallbackKind,
        string reason,
        string? context,
        string? astNodeKind)
    {
        Diagnostics.Add(AddHirFallbackMetadata(
            CreateDiagnostic(message, span, code, DiagnosticMessages.HirFallbackLabel),
            fallbackKind,
            reason,
            nameof(HirError),
            context,
            astNodeKind));
        return new HirError
        {
            Span = span,
            TypeId = new TypeId(BaseTypes.UnitId),
            Reason = message
        };
    }

    private static Diagnostic.Diagnostic CreateDiagnostic(string message, SourceSpan span, string code, string label)
    {
        var diag = Diagnostic.Diagnostic.Error(message, code);
        if (HasSpan(span))
        {
            diag.WithLabel(span, label);
        }

        return diag;
    }

    private static Diagnostic.Diagnostic AddHirFallbackMetadata(
        Diagnostic.Diagnostic diagnostic,
        string fallbackKind,
        string reason,
        string hirNodeKind,
        string? context = null,
        string? astNodeKind = null)
    {
        diagnostic
            .WithMetadata("phase", "hir")
            .WithMetadata("fallbackKind", fallbackKind)
            .WithMetadata("reason", reason)
            .WithMetadata("hirNodeKind", hirNodeKind);

        if (!string.IsNullOrWhiteSpace(context))
        {
            diagnostic.WithMetadata("context", context);
        }

        if (!string.IsNullOrWhiteSpace(astNodeKind))
        {
            diagnostic.WithMetadata("astNodeKind", astNodeKind);
        }

        return diagnostic;
    }

    private static bool HasSpan(SourceSpan span)
    {
        return span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }

    private HirNode ConvertInfixCall(InfixCallExpr infixCall)
    {
        // x `f` y  →  f(x, y)
        var left = ConvertExprOrFallback(infixCall.Left, "infix call left operand", infixCall.Span);
        var right = ConvertExprOrFallback(infixCall.Right, "infix call right operand", infixCall.Span);

        var functionRef = new HirVar
        {
            Name = infixCall.FunctionName,
            SymbolId = infixCall.FunctionSymbolId,
            Span = infixCall.Span,
            TypeId = GetTypeId(infixCall)
        };
        return BuildCallableApplication(
            functionRef,
            [left, right],
            loweredTarget: null,
            infixCall.Span,
            GetTypeId(infixCall),
            HirCallSurfaceSyntax.Infix);
    }

    private HirNode ConvertPipe(BinaryExpr bin)
    {
        // x |> f  →  f(x)
        var left = ConvertExprOrFallback(bin.Left, "pipe left operand", bin.Span);
        var right = ConvertExprOrFallback(bin.Right, "pipe right operand", bin.Span);

        return BuildCallableApplication(
            right,
            [left],
            bin.Right,
            bin.Span,
            GetTypeId(bin),
            HirCallSurfaceSyntax.Pipe);
    }

    private HirNode BuildStdlibCall(
        BinaryExpr bin,
        string funcName,
        bool swapArgs,
        CompilerSemanticRole role)
    {
        // a <op> b  →  Module.func(first, second)
        // swapArgs=false: first=left, second=right
        // swapArgs=true: first=right, second=left
        var left = ConvertExprOrFallback(bin.Left, "operator left operand", bin.Span);
        var right = ConvertExprOrFallback(bin.Right, "operator right operand", bin.Span);

        var first = swapArgs ? right : left;
        var second = swapArgs ? left : right;
        var functionRef = BuildCompilerRoleFunctionVar(role, funcName, bin.Span);
        return BuildCallableApplication(
            functionRef,
            [first, second],
            loweredTarget: null,
            bin.Span,
            GetTypeId(bin),
            HirCallSurfaceSyntax.OperatorDesugaring);
    }

    internal HirVar BuildCompilerRoleFunctionVar(
        CompilerSemanticRole role,
        string fallbackName,
        SourceSpan span)
    {
        var matches = _symbolTable.Symbols.Values
            .OfType<FuncSymbol>()
            .Where(symbol => symbol.CompilerSemanticRole == role)
            .OrderBy(symbol => symbol.Id.Value)
            .ToArray();
        if (matches.Length != 1)
        {
            Diagnostics.Add(CreateDiagnostic(
                $"Prelude Core Image must register exactly one function for compiler role '{role}', found {matches.Length}.",
                span,
                "E5208",
                "compiler-owned elaboration role resolution failed"));
        }

        var selected = matches.FirstOrDefault();
        return new HirVar
        {
            Name = selected?.Name ?? fallbackName,
            SymbolId = selected?.Id ?? SymbolId.None,
            Span = span,
            TypeId = TypeId.None
        };
    }

    private HirNode ConvertBinaryExpr(BinaryExpr bin)
    {
        if (bin.Operator == Ast.BinaryOp.Pipe)
        {
            return ConvertPipe(bin);
        }

        // `xs :+ last` desugars to `Seq.append(xs)(Seq.singleton(last))`,
        // a two-call shape the single-call StdlibOperatorDesugaring table cannot express.
        if (bin.Operator == Ast.BinaryOp.AppendLast)
        {
            return BuildAppendLastCall(bin);
        }

        if (StdlibOperatorDesugaring.TryGetValue(bin.Operator, out var desugaring))
        {
            return BuildStdlibCall(
                bin,
                desugaring.FuncName,
                desugaring.SwapArgs,
                desugaring.Role);
        }

        return new HirBinOp
        {
            Operator = AstToHirBinaryOp.GetValueOrDefault(bin.Operator, Hir.BinaryOp.Add),
            Left = ConvertExprOrFallback(bin.Left, "binary left operand", bin.Span),
            Right = ConvertExprOrFallback(bin.Right, "binary right operand", bin.Span),
            Span = bin.Span,
            TypeId = GetTypeId(bin)
        };
    }

    private HirNode BuildAppendLastCall(BinaryExpr bin)
    {
        var listArg = ConvertExprOrFallback(bin.Left, "operator left operand", bin.Span);
        var singletonFunc = BuildCompilerRoleFunctionVar(
            CompilerSemanticRole.AppendLastSingleton,
            "singleton",
            bin.Span);
        var singletonCall = BuildCallableApplication(
            singletonFunc,
            [ConvertExprOrFallback(bin.Right, "operator right operand", bin.Span)],
            loweredTarget: null,
            bin.Span,
            TypeId.None,
            HirCallSurfaceSyntax.OperatorDesugaring);
        var appendFunc = BuildCompilerRoleFunctionVar(
            CompilerSemanticRole.AppendLastAppend,
            "append",
            bin.Span);
        return BuildCallableApplication(
            appendFunc,
            [listArg, singletonCall],
            loweredTarget: null,
            bin.Span,
            GetTypeId(bin),
            HirCallSurfaceSyntax.OperatorDesugaring);
    }

    private TypeId GetTypeId(EidosAstNode? node) => _typeRegistry.GetTypeId(node);
    private bool TryGetResolvedInferredType(EidosAstNode? node, out DrivenType resolvedType) => _typeRegistry.TryGetResolvedInferredType(node, out resolvedType);
    private TypeId ResolveDeclaredTypeId(SymbolId symbolId) => _typeRegistry.ResolveDeclaredTypeId(symbolId);
    private TypeId GetTypeTypeId(DrivenType type) => _typeRegistry.GetTypeTypeId(type);
    private void CollectConstructorLayoutsFromAdtDef(AdtDef adt) => _typeRegistry.CollectConstructorLayoutsFromAdtDef(adt);
}
