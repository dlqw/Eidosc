using System.Diagnostics.CodeAnalysis;

namespace Eidosc;

/// <summary>
/// SyntaxKind 辅助方法：分类判断和从文本反查。
/// </summary>
public static class SyntaxKindHelper
{
    // ---- 分类判断 ----

    public static bool IsKeyword(this SyntaxKind kind)
        => kind >= SyntaxKind.KwModule && kind <= SyntaxKind.KwQuote;

    public static bool IsLiteral(this SyntaxKind kind)
        => kind is SyntaxKind.NumberLiteral or SyntaxKind.StringLiteral
            or SyntaxKind.CharLiteral or SyntaxKind.BooleanLiteral;

    public static bool IsPunctuation(this SyntaxKind kind)
        => kind >= SyntaxKind.PtBacktick && kind <= SyntaxKind.PtAt;

    public static bool IsOperator(this SyntaxKind kind)
        => kind >= SyntaxKind.OpArrow && kind <= SyntaxKind.OpQuestion;

    public static bool IsAnyIdentifier(this SyntaxKind kind)
        => kind is SyntaxKind.Identifier or SyntaxKind.OperatorIdentifier;

    // ---- 文本 → SyntaxKind 反查 ----

    private static readonly Dictionary<string, SyntaxKind> s_textToKind = new()
    {
        // 关键字
        ["module"] = SyntaxKind.KwModule,
        ["import"] = SyntaxKind.KwImport,
        ["export"] = SyntaxKind.KwExport,
        ["let"] = SyntaxKind.KwLet,
        ["func"] = SyntaxKind.KwFunc,
        ["ability"] = SyntaxKind.KwEffect,
        ["effect"] = SyntaxKind.KwEffect,
        ["effects"] = SyntaxKind.KwEffects,
        ["type"] = SyntaxKind.KwType,
        ["trait"] = SyntaxKind.KwTrait,
        ["fn"] = SyntaxKind.KwFn,
        ["if"] = SyntaxKind.KwIf,
        ["then"] = SyntaxKind.KwThen,
        ["else"] = SyntaxKind.KwElse,
        ["while"] = SyntaxKind.KwWhile,
        ["loop"] = SyntaxKind.KwLoop,
        ["match"] = SyntaxKind.KwMatch,
        ["when"] = SyntaxKind.KwWhen,
        ["return"] = SyntaxKind.KwReturn,
        ["need"] = SyntaxKind.KwNeed,
        ["requires"] = SyntaxKind.KwRequires,
        ["break"] = SyntaxKind.KwBreak,
        ["continue"] = SyntaxKind.KwContinue,
        ["as"] = SyntaxKind.KwAs,
        ["ref"] = SyntaxKind.KwRef,
        ["mut"] = SyntaxKind.KwMut,
        ["mref"] = SyntaxKind.KwMref,
        ["do"] = SyntaxKind.KwDo,
        ["unreachable"] = SyntaxKind.KwUnreachable,
        ["quote"] = SyntaxKind.KwQuote,

        // 运算符
        ["->"] = SyntaxKind.OpArrow,
        ["=>"] = SyntaxKind.OpFatArrow,
        [":="] = SyntaxKind.OpAssign,
        ["="] = SyntaxKind.OpBind,
        ["::"] = SyntaxKind.OpColonColon,
        ["|"] = SyntaxKind.OpPipe,
        ["&"] = SyntaxKind.OpPatternAnd,
        ["+"] = SyntaxKind.OpPlus,
        ["++"] = SyntaxKind.OpConcat,
        ["-"] = SyntaxKind.OpMinus,
        ["*"] = SyntaxKind.OpStar,
        ["/"] = SyntaxKind.OpSlash,
        ["%"] = SyntaxKind.OpPercent,
        ["=="] = SyntaxKind.OpEq,
        ["!="] = SyntaxKind.OpNe,
        ["<"] = SyntaxKind.OpLt,
        [">"] = SyntaxKind.OpGt,
        ["<="] = SyntaxKind.OpLe,
        [">="] = SyntaxKind.OpGe,
        [".."] = SyntaxKind.OpRange,
        ["&&"] = SyntaxKind.OpAnd,
        ["||"] = SyntaxKind.OpOr,
        ["<-"] = SyntaxKind.OpLeftArrow,
        ["!"] = SyntaxKind.OpNot,
        ["|>"] = SyntaxKind.OpPipeForward,
        [">>="] = SyntaxKind.OpBindArrow,
        ["??"] = SyntaxKind.OpCoalesce,
        [">>>"] = SyntaxKind.OpComposeR,
        ["<<<"] = SyntaxKind.OpComposeL,
        ["<$>"] = SyntaxKind.OpFmap,
        ["<*>"] = SyntaxKind.OpAp,
        ["<>"] = SyntaxKind.OpAppend,
        ["+:"] = SyntaxKind.OpPrepend,
        [":+"] = SyntaxKind.OpAppendLast,
        ["?"] = SyntaxKind.OpQuestion,

        // 标点
        ["`"] = SyntaxKind.PtBacktick,
        ["("] = SyntaxKind.PtLParen,
        [")"] = SyntaxKind.PtRParen,
        ["["] = SyntaxKind.PtLBrack,
        ["]"] = SyntaxKind.PtRBrack,
        ["{"] = SyntaxKind.PtLBrace,
        ["}"] = SyntaxKind.PtRBrace,
        [","] = SyntaxKind.PtComma,
        [";"] = SyntaxKind.PtSemi,
        ["."] = SyntaxKind.PtDot,
        ["_"] = SyntaxKind.PtUnderscore,
        [":"] = SyntaxKind.PtColon,
        ["@"] = SyntaxKind.PtAt,
    };

    /// <summary>
    /// 从关键字/运算符/标点的文本反查 SyntaxKind。
    /// 仅适用于 KeywordRule 管理的 token（关键字、运算符、标点）。
    /// </summary>
    public static bool TryFromText(string text, out SyntaxKind kind)
        => s_textToKind.TryGetValue(text, out kind);
}
