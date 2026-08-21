using System.Numerics;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    /// <summary>
    /// 无后缀整数/浮点字面量按期望的基元数值类型适配（Rust 语义）。
    /// 显式后缀不参与；超出目标类型范围则不适配（交给统一检查报错，不静默窄化）。
    /// 说明：负字面量按有符号目标类型的 |min| 适配；显式后缀不参与。
    /// </summary>
    private bool TryAdaptLiteralToExpectedType(EidosAstNode expr, TyCon expected, out Type adapted)
    {
        adapted = expected;

        if (expr is not LiteralExpr literal ||
            literal.Kind is not (LiteralKind.Integer or LiteralKind.Float) ||
            literal.TypeSuffix != LiteralTypeSuffix.None ||
            !TryGetSuffixForBaseType(expected, out var target, out var arbitraryWidth) ||
            target == LiteralTypeSuffix.None)
        {
            return false;
        }

        if (literal.Kind == LiteralKind.Float)
        {
            if (!LiteralSuffixTable.IsFloat(target))
            {
                return false;
            }

            double d = literal.Value switch
            {
                double dd => dd,
                float ff => ff,
                _ => 0
            };
            literal.SetValueAndKind(
                target == LiteralTypeSuffix.Float32 ? (object)(float)d : d,
                LiteralKind.Float,
                target);
            return true;
        }

        // 整数适配（负字面量由带符号 token 路径直接携带负值）
        bool isNegative = literal.Value switch
        {
            long negativeLong when negativeLong < 0 => true,
            BigInteger negativeBig when negativeBig.Sign < 0 => true,
            _ => false
        };
        if (!literal.TryGetBigIntegerMagnitude(out BigInteger mag))
        {
            if (!isNegative)
            {
                return false;
            }

            mag = literal.Value switch
            {
                long negativeLong => -(BigInteger)negativeLong,
                BigInteger negativeBig => -negativeBig,
                _ => BigInteger.Zero
            };
        }

        // 整数 → 浮点目标（Rust 语义：`f64 / 2` 中的 2 转成浮点）
        if (LiteralSuffixTable.IsFloat(target))
        {
            object floatValue = target == LiteralTypeSuffix.Float32
                ? (object)(float)(isNegative ? -mag : mag)
                : (double)(isNegative ? -mag : mag);
            literal.SetValueAndKind(floatValue, LiteralKind.Float, target);
            return true;
        }

        if (isNegative)
        {
            if (!LiteralSuffixTable.IsSigned(target) ||
                !LiteralSuffixTable.TryGetBigIntegerSignedMinMagnitude(target, arbitraryWidth ?? 0, out BigInteger negativeMin) ||
                mag > negativeMin)
            {
                return false;
            }

            literal.SetValueAndKind(
                target == LiteralTypeSuffix.IntArbitrary
                    ? (object)-mag
                    : mag == long.MaxValue + (BigInteger)1
                        ? (object)long.MinValue
                        : -(long)mag,
                LiteralKind.Integer,
                target,
                arbitraryWidth);
            return true;
        }

        if (!LiteralSuffixTable.TryGetBigIntegerMagnitudeLimit(target, arbitraryWidth ?? 0, out BigInteger max))
        {
            return false;
        }

        if (mag > max)
        {
            return false; // 超出目标范围：不窄化，交给统一检查报错
        }

        object value = mag <= long.MaxValue
            ? (object)(long)mag
            : mag <= ulong.MaxValue
                ? (object)(ulong)mag
                : mag;
        literal.SetValueAndKind(value, LiteralKind.Integer, target, arbitraryWidth);
        return true;
    }

    private static bool TryGetSuffixForBaseType(
        TyCon con,
        out LiteralTypeSuffix suffix,
        out int? arbitraryWidth)
    {
        arbitraryWidth = null;
        if (BaseTypes.TryParseIntegerTypeName(con.Name, out var unsigned, out var width))
        {
            suffix = unsigned ? LiteralTypeSuffix.UIntArbitrary : LiteralTypeSuffix.IntArbitrary;
            arbitraryWidth = width;
            return true;
        }

        suffix = con.Name switch
        {
            WellKnownStrings.BuiltinTypes.Int8 => LiteralTypeSuffix.Int8,
            WellKnownStrings.BuiltinTypes.Int16 => LiteralTypeSuffix.Int16,
            WellKnownStrings.BuiltinTypes.Int32 => LiteralTypeSuffix.Int32,
            WellKnownStrings.BuiltinTypes.Int64 => LiteralTypeSuffix.Int64,
            WellKnownStrings.BuiltinTypes.UInt8 => LiteralTypeSuffix.UInt8,
            WellKnownStrings.BuiltinTypes.UInt16 => LiteralTypeSuffix.UInt16,
            WellKnownStrings.BuiltinTypes.UInt32 => LiteralTypeSuffix.UInt32,
            WellKnownStrings.BuiltinTypes.UInt64 => LiteralTypeSuffix.UInt64,
            WellKnownStrings.BuiltinTypes.Float32 => LiteralTypeSuffix.Float32,
            WellKnownStrings.BuiltinTypes.Float64 => LiteralTypeSuffix.Float64,
            _ => LiteralTypeSuffix.None
        };
        return suffix != LiteralTypeSuffix.None;
    }

    /// <summary>
    /// 二元运算位的字面量适配（Rust 语义）：当一侧是无后缀数值字面量、另一侧是
    /// 可判定的基元数值类型（标识符/显式字面量锚点）时，把字面量适配到该类型。
    /// 命名窄值之间不提供隐式提升（混宽仍需显式转换）。
    /// </summary>
    private bool TryAdaptBinaryLiteralOperands(BinaryExpr binary, out Type leftType, out Type rightType)
    {
        leftType = BaseTypes.Int;
        rightType = BaseTypes.Int;

        // 管道符号走 TypeInferer 自己的类型导向重载解析（按左操作数选 score），
        // 不能被字面量适配的 IdentifierExpr 锚点抢先消费（歧义误报）。
        if (binary.Operator == BinaryOp.Pipe)
        {
            return false;
        }

        bool leftIsLiteral = IsAdaptableNumericLiteral(binary.Left!);
        bool rightIsLiteral = IsAdaptableNumericLiteral(binary.Right!);

        if (leftIsLiteral && !rightIsLiteral &&
            CanBeTypeAnchor(binary.Right!) &&
            TryAdaptLiteralToAnchor(binary.Left!, binary.Right!, out leftType))
        {
            rightType = SafeInferExpression(binary.Right!);
            return true;
        }

        if (rightIsLiteral && !leftIsLiteral &&
            CanBeTypeAnchor(binary.Left!) &&
            TryAdaptLiteralToAnchor(binary.Right!, binary.Left!, out rightType))
        {
            leftType = SafeInferExpression(binary.Left!);
            return true;
        }

        return false;
    }

    private static bool IsAdaptableNumericLiteral(EidosAstNode node) =>
        node is LiteralExpr { TypeSuffix: LiteralTypeSuffix.None } lit &&
        lit.Kind is LiteralKind.Integer or LiteralKind.Float;

    private static bool CanBeTypeAnchor(EidosAstNode node) =>
        node is IdentifierExpr or LiteralExpr;

    private bool TryAdaptLiteralToAnchor(EidosAstNode literalNode, EidosAstNode anchorNode, out Type adapted)
    {
        adapted = BaseTypes.Int;
        var anchorType = _substitution.Apply(SafeInferExpression(anchorNode));
        return anchorType is TyCon anchorCon &&
               TryAdaptLiteralToExpectedType(literalNode, anchorCon, out adapted);
    }
}
