using Eidosc.Symbols;
using Eidosc.Ast.Expressions;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Closure;

/// <summary>
/// 闭包环境结构体生成器 - 为闭包生成环境结构体类型
/// </summary>
public sealed class ClosureEnvironmentGenerator
{
    private readonly SymbolTable _symbolTable;
    private int _envCounter;

    public ClosureEnvironmentGenerator(SymbolTable symbolTable)
    {
        _symbolTable = symbolTable;
    }

    /// <summary>
    /// 为 Lambda 表达式生成闭包环境类型
    /// </summary>
    /// <param name="lambda">Lambda 表达式</param>
    /// <param name="freeVariables">自由变量集合</param>
    /// <returns>闭包环境类型信息</returns>
    public ClosureEnvironmentInfo GenerateEnvironment(LambdaExpr lambda, HashSet<string> freeVariables)
    {
        var envName = $"{WellKnownStrings.InternalNames.ClosureEnvPrefix}{_envCounter++}";
        var fields = new List<ClosureEnvField>();

        foreach (var varName in freeVariables.OrderBy(v => v))
        {
            var fieldType = GetVariableType(varName);
            fields.Add(new ClosureEnvField(varName, fieldType));
        }

        // 生成环境结构体类型
        var envType = CreateEnvironmentType(fields);

        return new ClosureEnvironmentInfo
        {
            EnvironmentName = envName,
            Fields = fields,
            EnvironmentType = envType,
            SourceLambda = lambda
        };
    }

    /// <summary>
    /// 获取变量的类型（简化版本）
    /// </summary>
    private Eidosc.Types.Type GetVariableType(string varName)
    {
        // 查找符号表获取变量类型
        var symbolId = _symbolTable.LookupValue(varName);
        if (symbolId.HasValue)
        {
            var symbol = _symbolTable.GetSymbol(symbolId.Value);
            if (symbol is VarSymbol varSymbol && varSymbol.Type != TypeId.None)
            {
                // 返回类型引用
                return new TyCon { Name = varName, Id = varSymbol.Type };
            }
        }

        // 默认返回通用类型变量
        return new TyVar { Index = -1 };
    }

    /// <summary>
    /// 创建环境结构体类型
    /// </summary>
    private Eidosc.Types.Type CreateEnvironmentType(List<ClosureEnvField> fields)
    {
        // 创建表示闭包环境的元组类型
        if (fields.Count == 0)
        {
            return BaseTypes.Unit;
        }

        if (fields.Count == 1)
        {
            return fields[0].FieldType;
        }

        // 多字段使用元组类型
        var fieldTypes = fields.Select(f => f.FieldType).ToList();
        return new TyTuple { Elements = fieldTypes };
    }
}

/// <summary>
/// 闭包环境信息
/// </summary>
public sealed class ClosureEnvironmentInfo
{
    /// <summary>
    /// 环境结构体名称
    /// </summary>
    public required string EnvironmentName { get; init; }

    /// <summary>
    /// 环境字段列表
    /// </summary>
    public required List<ClosureEnvField> Fields { get; init; }

    /// <summary>
    /// 环境类型
    /// </summary>
    public required Eidosc.Types.Type EnvironmentType { get; init; }

    /// <summary>
    /// 源 Lambda 表达式
    /// </summary>
    public required LambdaExpr SourceLambda { get; init; }
}

/// <summary>
/// 闭包环境字段
/// </summary>
public sealed class ClosureEnvField
{
    /// <summary>
    /// 字段名称（变量名）
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 字段类型
    /// </summary>
    public Eidosc.Types.Type FieldType { get; }

    public ClosureEnvField(string name, Eidosc.Types.Type fieldType)
    {
        Name = name;
        FieldType = fieldType;
    }
}
