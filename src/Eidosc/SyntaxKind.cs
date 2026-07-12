namespace Eidosc;

/// <summary>
/// Token 类型枚举，替代 Terminal.DebugName 字符串匹配。
/// 每个 token 在创建时确定其 SyntaxKind，下游通过枚举比较识别 token 类型。
/// </summary>
public enum SyntaxKind
{
    // 特殊 token
    None = 0,
    Eof,
    Error,
    Comment,

    // 标识符
    Identifier,
    TypeIdentifier,
    OperatorIdentifier,

    // 字面量
    NumberLiteral,
    StringLiteral,
    CharLiteral,
    BooleanLiteral,

    // 关键字
    KwModule,
    KwImport,
    KwExport,
    KwLet,
    KwFunc,
    KwEffect,
    KwEffects,
    KwType,
    KwTrait,
    KwFn,
    KwIf,
    KwThen,
    KwElse,
    KwWhile,
    KwLoop,
    KwMatch,
    KwWhen,
    KwReturn,
    KwNeed,
    KwRequires,
    KwBreak,
    KwContinue,
    KwAs,
    KwRef,
    KwMut,
    KwMref,
    KwDo,
    KwUnreachable,

    // 运算符
    OpArrow,        // ->
    OpFatArrow,     // =>
    OpAssign,       // :=
    OpBind,         // =
    OpColonColon,   // ::
    OpPipe,         // |
    OpPatternAnd,   // &
    OpPlus,         // +
    OpConcat,       // ++
    OpMinus,        // -
    OpStar,         // *
    OpSlash,        // /
    OpPercent,      // %
    OpEq,           // ==
    OpNe,           // !=
    OpLt,           // <
    OpGt,           // >
    OpLe,           // <=
    OpGe,           // >=
    OpRange,        // ..
    OpAnd,          // &&
    OpOr,           // ||
    OpLeftArrow,    // <-
    OpNot,          // !
    OpPipeForward,  // |>
    OpBindArrow,    // >>=
    OpCoalesce,     // ??
    OpComposeR,     // >>>
    OpComposeL,     // <<<
    OpFmap,         // <$>
    OpAp,           // <*>
    OpAppend,       // <>
    OpPrepend,      // +:
    OpAppendLast,   // :+
    OpQuestion,     // ?

    // 标点
    PtBacktick,     // `
    PtLParen,       // (
    PtRParen,       // )
    PtLBrack,       // [
    PtRBrack,       // ]
    PtLBrace,       // {
    PtRBrace,       // }
    PtComma,        // ,
    PtSemi,         // ;
    PtDot,          // .
    PtUnderscore,   // _
    PtColon,        // :
    PtAt,           // @
}
