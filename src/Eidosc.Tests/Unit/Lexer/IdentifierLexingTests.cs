using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Lexer;

/// <summary>
/// 下划线相关标识符的词法回归：`_x` 是普通标识符、单独的 `_` 仍是通配符、
/// `__` 前缀按编译器保留报告 E3055（而不是在解析层碎成 `_` 记号）。
/// </summary>
public sealed class IdentifierLexingTests
{
    [Fact]
    public void UnderscorePrefixedIdentifier_CompilesAsValue()
    {
        CompilationHelper.Source("""
            _value :: Int = 1;

            main :: Unit -> Int
            {
                _ => _value
            }
            """).ShouldSucceed();
    }

    [Fact]
    public void UnderscoreIdentifier_AndWildcard_CoexistInBinder()
    {
        CompilationHelper.Source("""
            pick :: Int -> Int -> Int
            {
                _x, _ => _x
            }

            main :: Unit -> Int
            {
                _ => pick(7, 9)
            }
            """).ShouldSucceed();
    }

    [Fact]
    public void DoubleUnderscorePrefix_ReportsReservedPrefixDiagnostic()
    {
        CompilationHelper.Source("__value :: Int = 1;").ShouldReport("E3055");
    }
}
