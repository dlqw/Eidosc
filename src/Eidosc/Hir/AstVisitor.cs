using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;

namespace Eidosc.Hir;

/// <summary>
/// AST 访问者接口 - 支持泛型返回类型
/// </summary>
/// <typeparam name="TResult">访问结果类型</typeparam>
public interface IAstVisitor<TResult>
{
    /// <summary>
    /// 访问 AST 节点的默认实现
    /// </summary>
    /// <param name="node">AST 节点</param>
    /// <returns>访问结果</returns>
    TResult? Visit(EidosAstNode? node);

    /// <summary>
    /// 访问子节点
    /// </summary>
    /// <param name="node">AST 节点</param>
    /// <returns>子节点的访问结果</returns>
    IEnumerable<TResult> VisitChildren(EidosAstNode node);
}

/// <summary>
/// AST 访问者抽象基类 - 提供默认的深度优先遍历逻辑
/// </summary>
/// <typeparam name="TResult">访问结果类型</typeparam>
public abstract class AstVisitor<TResult> : IAstVisitor<TResult>
{
    /// <summary>
    /// 访问 AST 节点的默认实现 - 使用模式匹配分发到具体方法
    /// </summary>
    public virtual TResult? Visit(EidosAstNode? node)
    {
        if (node == null) return default;

        return node switch
        {
            // 声明节点
            FuncDef func => VisitFuncDef(func),
            FuncDecl funcDecl => VisitFuncDecl(funcDecl),
            EffectRequirementNode abilityRequirement => VisitEffectRequirementNode(abilityRequirement),
            AdtDef adt => VisitAdtDef(adt),
            TraitDef trait => VisitTraitDef(trait),
            EffectDef ability => VisitEffectDef(ability),
            LetDecl letDecl => VisitLetDecl(letDecl),
            LetQuestionDecl letQuestionDecl => VisitLetQuestionDecl(letQuestionDecl),
            ModuleDecl module => VisitModuleDecl(module),
            ImportDecl import => VisitImportDecl(import),

            // 表达式节点
            LiteralExpr lit => VisitLiteralExpr(lit),
            IdentifierExpr id => VisitIdentifierExpr(id),
            PathExpr path => VisitPathExpr(path),
            BinaryExpr bin => VisitBinaryExpr(bin),
            InfixCallExpr infixCall => VisitInfixCallExpr(infixCall),
            UnaryExpr unary => VisitUnaryExpr(unary),
            CallExpr call => VisitCallExpr(call),
            LambdaExpr lambda => VisitLambdaExpr(lambda),
            IfExpr ifExpr => VisitIfExpr(ifExpr),
            IfLetExpr ifLetExpr => VisitIfLetExpr(ifLetExpr),
            WhileLetExpr whileLetExpr => VisitWhileLetExpr(whileLetExpr),
            MatchExpr match => VisitMatchExpr(match),
            PatternGuardExpr patternGuard => VisitPatternGuardExpr(patternGuard),
            SequentialGuardExpr sequentialGuard => VisitSequentialGuardExpr(sequentialGuard),
            BlockExpr block => VisitBlockExpr(block),
            TupleExpr tuple => VisitTupleExpr(tuple),
            ListExpr list => VisitListExpr(list),
            CtorExpr ctor => VisitCtorExpr(ctor),
            RecordUpdateExpr recordUpdate => VisitRecordUpdateExpr(recordUpdate),
            ContextualRecordLiteralExpr contextualRecord => VisitContextualRecordLiteralExpr(contextualRecord),
            Assignment assign => VisitAssignment(assign),
            ReturnExpr ret => VisitReturnExpr(ret),
            LoopExpr loop => VisitLoopExpr(loop),
            BreakExpr breakExpr => VisitBreakExpr(breakExpr),
            ContinueExpr continueExpr => VisitContinueExpr(continueExpr),
            IndexExpr index => VisitIndexExpr(index),
            MethodCallExpr methodCall => VisitMethodCallExpr(methodCall),
            QuoteExpr quote => VisitQuoteExpr(quote),
            ExpandExpr expansion => VisitExpandExpr(expansion),
            QuoteTokenPart token => VisitQuoteTokenPart(token),
            QuoteSplicePart splice => VisitQuoteSplicePart(splice),

            // 模式节点
            ExpandPattern expansion => VisitExpandPattern(expansion),
            VarPattern varPat => VisitVarPattern(varPat),
            CtorPattern ctorPat => VisitCtorPattern(ctorPat),
            WildcardPattern wildcard => VisitWildcardPattern(wildcard),
            LiteralPattern litPat => VisitLiteralPattern(litPat),
            TuplePattern tuplePat => VisitTuplePattern(tuplePat),
            ListPattern listPat => VisitListPattern(listPat),
            OrPattern orPat => VisitOrPattern(orPat),
            AndPattern andPat => VisitAndPattern(andPat),
            NotPattern notPat => VisitNotPattern(notPat),
            RangePattern rangePat => VisitRangePattern(rangePat),
            ViewPattern viewPat => VisitViewPattern(viewPat),
            AsPattern asPat => VisitAsPattern(asPat),
            FieldPattern fieldPat => VisitFieldPattern(fieldPat),

            // 类型节点 - 注意：具体类型必须放在 TypeNode 之前
            ExpandType expansion => VisitExpandType(expansion),
            ArrowType arrow => VisitArrowType(arrow),
            EffectfulType effectful => VisitEffectfulType(effectful),
            TupleType tupleType => VisitTupleType(tupleType),
            TypePath typePath => VisitTypePath(typePath),
            WildcardType wildcardType => VisitWildcardType(wildcardType),
            TypeNode typeNode => VisitTypeNode(typeNode),
            TraitRef traitRef => VisitTraitRef(traitRef),
            TypeParam typeParam => VisitTypeParam(typeParam),
            Kind kind => VisitKind(kind),

            // 默认情况
            _ => DefaultVisit(node)
        };
    }

    /// <summary>
    /// 访问子节点 - 默认返回空序列
    /// </summary>
    public virtual IEnumerable<TResult> VisitChildren(EidosAstNode node)
    {
        foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
        {
            var result = Visit(child);
            if (result != null)
            {
                yield return result;
            }
        }
    }

    #region 声明节点访问方法

    /// <summary>
    /// 访问函数定义
    /// </summary>
    protected virtual TResult? VisitFuncDef(FuncDef node) => DefaultVisit(node);

    protected virtual TResult? VisitFuncDecl(FuncDecl node) => DefaultVisit(node);

    protected virtual TResult? VisitEffectRequirementNode(EffectRequirementNode node) => DefaultVisit(node);

    /// <summary>
    /// 访问 ADT 定义
    /// </summary>
    protected virtual TResult? VisitAdtDef(AdtDef node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Trait 定义
    /// </summary>
    protected virtual TResult? VisitTraitDef(TraitDef node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Effect 定义
    /// </summary>
    protected virtual TResult? VisitEffectDef(EffectDef node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Let 声明
    /// </summary>
    protected virtual TResult? VisitLetDecl(LetDecl node) => DefaultVisit(node);

    protected virtual TResult? VisitLetQuestionDecl(LetQuestionDecl node) => DefaultVisit(node);

    /// <summary>
    /// 访问模块声明
    /// </summary>
    protected virtual TResult? VisitModuleDecl(ModuleDecl node) => DefaultVisit(node);

    /// <summary>
    /// 访问导入声明
    /// </summary>
    protected virtual TResult? VisitImportDecl(ImportDecl node) => DefaultVisit(node);

    #endregion

    #region 表达式节点访问方法

    /// <summary>
    /// 访问字面量表达式
    /// </summary>
    protected virtual TResult? VisitLiteralExpr(LiteralExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问标识符表达式
    /// </summary>
    protected virtual TResult? VisitIdentifierExpr(IdentifierExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问路径表达式
    /// </summary>
    protected virtual TResult? VisitPathExpr(PathExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问二元表达式
    /// </summary>
    protected virtual TResult? VisitBinaryExpr(BinaryExpr node) => DefaultVisit(node);

    protected virtual TResult? VisitInfixCallExpr(InfixCallExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问一元表达式
    /// </summary>
    protected virtual TResult? VisitUnaryExpr(UnaryExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问调用表达式
    /// </summary>
    protected virtual TResult? VisitCallExpr(CallExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Lambda 表达式
    /// </summary>
    protected virtual TResult? VisitLambdaExpr(LambdaExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问 If 表达式
    /// </summary>
    protected virtual TResult? VisitIfExpr(IfExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问 If-Let 表达式
    /// </summary>
    protected virtual TResult? VisitIfLetExpr(IfLetExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问 While-Let 表达式
    /// </summary>
    protected virtual TResult? VisitWhileLetExpr(WhileLetExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Match 表达式
    /// </summary>
    protected virtual TResult? VisitMatchExpr(MatchExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Pattern Guard 表达式
    /// </summary>
    protected virtual TResult? VisitPatternGuardExpr(PatternGuardExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问顺序守卫表达式
    /// </summary>
    protected virtual TResult? VisitSequentialGuardExpr(SequentialGuardExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问块表达式
    /// </summary>
    protected virtual TResult? VisitBlockExpr(BlockExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问元组表达式
    /// </summary>
    protected virtual TResult? VisitTupleExpr(TupleExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问列表表达式
    /// </summary>
    protected virtual TResult? VisitListExpr(ListExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问构造器表达式
    /// </summary>
    protected virtual TResult? VisitCtorExpr(CtorExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问短 record update 表达式
    /// </summary>
    protected virtual TResult? VisitRecordUpdateExpr(RecordUpdateExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问上下文推断 record literal 表达式
    /// </summary>
    protected virtual TResult? VisitContextualRecordLiteralExpr(ContextualRecordLiteralExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问赋值表达式
    /// </summary>
    protected virtual TResult? VisitAssignment(Assignment node) => DefaultVisit(node);

    /// <summary>
    /// 访问返回表达式
    /// </summary>
    protected virtual TResult? VisitReturnExpr(ReturnExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问循环表达式
    /// </summary>
    protected virtual TResult? VisitLoopExpr(LoopExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Break 表达式
    /// </summary>
    protected virtual TResult? VisitBreakExpr(BreakExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Continue 表达式
    /// </summary>
    protected virtual TResult? VisitContinueExpr(ContinueExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问索引表达式
    /// </summary>
    protected virtual TResult? VisitIndexExpr(IndexExpr node) => DefaultVisit(node);

    /// <summary>
    /// 访问方法调用表达式
    /// </summary>
    protected virtual TResult? VisitMethodCallExpr(MethodCallExpr node) => DefaultVisit(node);

    protected virtual TResult? VisitQuoteExpr(QuoteExpr node) => DefaultVisit(node);

    protected virtual TResult? VisitExpandExpr(ExpandExpr node) => DefaultVisit(node);

    protected virtual TResult? VisitQuoteTokenPart(QuoteTokenPart node) => DefaultVisit(node);

    protected virtual TResult? VisitQuoteSplicePart(QuoteSplicePart node) => DefaultVisit(node);

    #endregion

    #region 模式节点访问方法

    protected virtual TResult? VisitExpandPattern(ExpandPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问变量模式
    /// </summary>
    protected virtual TResult? VisitVarPattern(VarPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问构造器模式
    /// </summary>
    protected virtual TResult? VisitCtorPattern(CtorPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问通配符模式
    /// </summary>
    protected virtual TResult? VisitWildcardPattern(WildcardPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问字面量模式
    /// </summary>
    protected virtual TResult? VisitLiteralPattern(LiteralPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问元组模式
    /// </summary>
    protected virtual TResult? VisitTuplePattern(TuplePattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问列表模式
    /// </summary>
    protected virtual TResult? VisitListPattern(ListPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Or 模式
    /// </summary>
    protected virtual TResult? VisitOrPattern(OrPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问 And 模式
    /// </summary>
    protected virtual TResult? VisitAndPattern(AndPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Not 模式
    /// </summary>
    protected virtual TResult? VisitNotPattern(NotPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Range 模式
    /// </summary>
    protected virtual TResult? VisitRangePattern(RangePattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问 View 模式
    /// </summary>
    protected virtual TResult? VisitViewPattern(ViewPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问 As 模式
    /// </summary>
    protected virtual TResult? VisitAsPattern(AsPattern node) => DefaultVisit(node);

    /// <summary>
    /// 访问字段模式
    /// </summary>
    protected virtual TResult? VisitFieldPattern(FieldPattern node) => DefaultVisit(node);

    #endregion

    #region 类型节点访问方法

    protected virtual TResult? VisitExpandType(ExpandType node) => DefaultVisit(node);

    /// <summary>
    /// 访问类型节点
    /// </summary>
    protected virtual TResult? VisitTypeNode(TypeNode node) => DefaultVisit(node);

    /// <summary>
    /// 访问箭头类型
    /// </summary>
    protected virtual TResult? VisitArrowType(ArrowType node) => DefaultVisit(node);

    /// <summary>
    /// 访问效应类型
    /// </summary>
    protected virtual TResult? VisitEffectfulType(EffectfulType node) => DefaultVisit(node);

    /// <summary>
    /// 访问元组类型
    /// </summary>
    protected virtual TResult? VisitTupleType(TupleType node) => DefaultVisit(node);

    /// <summary>
    /// 访问类型路径
    /// </summary>
    protected virtual TResult? VisitTypePath(TypePath node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Trait 引用
    /// </summary>
    protected virtual TResult? VisitTraitRef(TraitRef node) => DefaultVisit(node);

    /// <summary>
    /// 访问类型参数
    /// </summary>
    protected virtual TResult? VisitTypeParam(TypeParam node) => DefaultVisit(node);

    /// <summary>
    /// 访问通配符类型
    /// </summary>
    protected virtual TResult? VisitWildcardType(WildcardType node) => DefaultVisit(node);

    /// <summary>
    /// 访问 Kind
    /// </summary>
    protected virtual TResult? VisitKind(Kind node) => DefaultVisit(node);

    #endregion

    /// <summary>
    /// 默认访问方法 - 当没有具体的访问方法时调用
    /// </summary>
    protected virtual TResult? DefaultVisit(EidosAstNode node)
    {
        // 默认实现：访问所有子节点并返回第一个非空结果
        foreach (var result in VisitChildren(node))
        {
            if (result != null)
                return result;
        }
        return default;
    }
}

/// <summary>
/// 简单的 AST 遍历器 - 用于遍历所有节点而不返回结果
/// </summary>
public abstract class AstWalker : AstVisitor<bool>
{
    /// <summary>
    /// 访问节点 - 返回 true 表示继续遍历
    /// </summary>
    public new bool Visit(EidosAstNode? node)
    {
        if (node == null) return true;

        // 调用具体节点的处理方法
        var shouldContinue = OnVisitNode(node);

        // 如果需要继续，遍历子节点
        if (shouldContinue)
        {
            foreach (var _ in VisitChildren(node))
            {
            }
        }

        return shouldContinue;
    }

    /// <summary>
    /// 当访问任何节点时调用 - 子类重写此方法来实现自定义逻辑
    /// </summary>
    /// <param name="node">当前节点</param>
    /// <returns>true 表示继续遍历子节点，false 表示停止</returns>
    protected abstract bool OnVisitNode(EidosAstNode node);

    /// <summary>
    /// 访问子节点
    /// </summary>
    public override IEnumerable<bool> VisitChildren(EidosAstNode node)
    {
        foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
        {
            yield return Visit(child);
        }
    }
}

/// <summary>
/// AST 节点收集器 - 收集特定类型的节点
/// </summary>
/// <typeparam name="T">要收集的节点类型</typeparam>
public sealed class AstNodeCollector<T> : AstWalker where T : EidosAstNode
{
    private readonly List<T> _collected = [];

    /// <summary>
    /// 收集到的节点
    /// </summary>
    public List<T> Collected => _collected;

    /// <summary>
    /// 收集 AST 中所有指定类型的节点
    /// </summary>
    public static IReadOnlyList<T> Collect(EidosAstNode root)
    {
        var collector = new AstNodeCollector<T>();
        collector.Visit(root);
        return collector.Collected;
    }

    protected override bool OnVisitNode(EidosAstNode node)
    {
        if (node is T typed)
        {
            _collected.Add(typed);
        }
        return true; // 继续遍历
    }
}
