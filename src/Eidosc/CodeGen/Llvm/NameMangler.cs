using System.Security.Cryptography;
using System.Text;

namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// 名称修饰器 - 生成唯一的 LLVM 标识符
/// </summary>
public sealed class NameMangler
{
    private int _tempCounter = 0;
    private int _labelCounter = 0;
    private readonly Dictionary<string, string> _mangledNames = new();

    /// <summary>
    /// 修饰函数名
    /// </summary>
    /// <param name="moduleName">模块名</param>
    /// <param name="funcName">函数名</param>
    /// <param name="typeParams">类型参数（可选）</param>
    /// <returns>修饰后的名称</returns>
    public string MangleFunctionName(string moduleName, string funcName, List<Types.Type>? typeParams = null)
    {
        var typeHash = BuildTypeHashKeyPart(typeParams);
        var key = $"func:{moduleName}:{funcName}:{typeHash}";

        if (_mangledNames.TryGetValue(key, out var cached))
            return cached;

        var sb = new StringBuilder();

        // 使用 eidos_ 前缀
        sb.Append(WellKnownStrings.Mangling.Prefix);

        // 添加模块全名
        if (!string.IsNullOrEmpty(moduleName))
        {
            sb.Append(SanitizeIdentifier(NormalizeModuleName(moduleName)));
            sb.Append('_');
        }

        // 添加函数名
        sb.Append(SanitizeIdentifier(funcName));

        // 如果有类型参数，添加类型哈希
        if (typeParams != null && typeParams.Count > 0)
        {
            sb.Append('_');
            sb.Append(typeHash);
        }

        var result = sb.ToString();
        _mangledNames[key] = result;
        return result;
    }

    /// <summary>
    /// 修饰函数实例名（用于同名函数的不同实例）
    /// </summary>
    public string MangleFunctionInstanceName(string moduleName, string funcName, string instanceKey)
    {
        var normalizedInstanceKey = string.IsNullOrWhiteSpace(instanceKey) ? "instance" : instanceKey;
        var key = $"funcinst:{moduleName}:{funcName}:{normalizedInstanceKey}";

        if (_mangledNames.TryGetValue(key, out var cached))
            return cached;

        var sb = new StringBuilder();
        sb.Append(WellKnownStrings.Mangling.Prefix);

        if (!string.IsNullOrEmpty(moduleName))
        {
            sb.Append(SanitizeIdentifier(NormalizeModuleName(moduleName)));
            sb.Append('_');
        }

        sb.Append(SanitizeIdentifier(funcName));
        sb.Append("_i");
        sb.Append(SanitizeIdentifier(normalizedInstanceKey));

        var result = sb.ToString();
        _mangledNames[key] = result;
        return result;
    }

    /// <summary>
    /// 修饰全局变量名
    /// </summary>
    public string MangleGlobalName(string moduleName, string varName)
    {
        var key = $"global:{moduleName}:{varName}";

        if (_mangledNames.TryGetValue(key, out var cached))
            return cached;

        var sb = new StringBuilder();

        sb.Append(WellKnownStrings.Mangling.GlobalPrefix);

        if (!string.IsNullOrEmpty(moduleName))
        {
            sb.Append(SanitizeIdentifier(NormalizeModuleName(moduleName)));
            sb.Append('_');
        }

        sb.Append(SanitizeIdentifier(varName));

        var result = sb.ToString();
        _mangledNames[key] = result;
        return result;
    }

    /// <summary>
    /// 修饰类型名（用于结构体、ADT 等）
    /// </summary>
    public string MangleTypeName(string moduleName, string typeName, List<Types.Type>? typeArgs = null)
    {
        var typeHash = BuildTypeHashKeyPart(typeArgs);
        var key = $"type:{moduleName}:{typeName}:{typeHash}";

        if (_mangledNames.TryGetValue(key, out var cached))
            return cached;

        var sb = new StringBuilder();

        sb.Append(WellKnownStrings.Mangling.TempPrefix);

        if (!string.IsNullOrEmpty(moduleName))
        {
            sb.Append(SanitizeIdentifier(NormalizeModuleName(moduleName)));
            sb.Append('_');
        }

        sb.Append(SanitizeIdentifier(typeName));

        // 类型参数
        if (typeArgs != null && typeArgs.Count > 0)
        {
            sb.Append('_');
            sb.Append(typeHash);
        }

        var result = sb.ToString();
        _mangledNames[key] = result;
        return result;
    }

    /// <summary>
    /// 生成临时变量名
    /// </summary>
    public string NewTempName(string? prefix = null)
    {
        return $"{prefix ?? "tmp"}_{_tempCounter++}";
    }

    /// <summary>
    /// 生成基本块标签
    /// </summary>
    public string NewLabel(string? prefix = null)
    {
        return $"{prefix ?? "bb"}_{_labelCounter++}";
    }

    /// <summary>
    /// 重置计数器（用于新函数）
    /// </summary>
    public void ResetCounters()
    {
        _tempCounter = 0;
        _labelCounter = 0;
    }

    /// <summary>
    /// 清除所有缓存的名称
    /// </summary>
    public void ClearCache()
    {
        _mangledNames.Clear();
        ResetCounters();
    }

    /// <summary>
    /// 清理标识符，移除或替换非法字符（如 ':', '&lt;', '&gt;' 等在 LLVM IR 中无效的字符）。
    /// </summary>
    internal static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";

        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
            else if (c == '-')
            {
                sb.Append('_');
            }
            else
            {
                // 使用 Unicode 转义
                sb.Append($"_u{((int)c):X4}_");
            }
        }

        // 确保不以数字开头
        if (sb.Length > 0 && char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    private static string NormalizeModuleName(string moduleName)
    {
        return moduleName
            .Replace(WellKnownStrings.Separators.Path, WellKnownStrings.InternalNames.ModuleSeparator, StringComparison.Ordinal)
            .Replace(WellKnownStrings.Separators.ModulePath, WellKnownStrings.InternalNames.ModuleSeparator, StringComparison.Ordinal)
            .Replace(".", WellKnownStrings.InternalNames.ModuleSeparator, StringComparison.Ordinal);
    }

    /// <summary>
    /// 计算类型列表的哈希值
    /// </summary>
    private static string BuildTypeHashKeyPart(List<Types.Type>? types)
    {
        if (types == null || types.Count == 0)
        {
            return "no_types";
        }

        return ComputeTypeHash(types);
    }

    private static string ComputeTypeHash(IReadOnlyList<Types.Type> types)
    {
        var typeStr = string.Join(",", types.Select(TypeToString));
        var bytes = Encoding.UTF8.GetBytes(typeStr);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// 将类型转换为字符串表示（用于哈希计算）
    /// </summary>
    private static string TypeToString(Types.Type type)
    {
        return type switch
        {
            Types.TyVar var => var.Instance != null ? TypeToString(var.Instance) : $"t{var.Index}",
            Types.TyCon con => FormatTyCon(con),
            Types.TyFun fun => $"({string.Join(",", fun.Params.Select(TypeToString))})->{TypeToString(fun.Result)}",
            Types.TyTuple tuple => $"({string.Join(",", tuple.Elements.Select(TypeToString))})",
            Types.TyRef => "Ref",
            Types.TyMutRef => "MRef",
            Types.TyShared => "Shared",
            Types.EffectRow abilitySet => $"{{{string.Join(",", abilitySet.Effects)}}}",
            Types.EffectTag abilityType => abilityType.ToString() ?? "unknown",
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static string FormatTyCon(Types.TyCon con)
    {
        if (con.Args.Count == 0 && con.ValueArgs.Count == 0)
        {
            return con.Name;
        }

        var valueArguments = con.ValueArgs.ToDictionary(static argument => argument.ParameterIndex);
        var argumentCount = con.Args.Count + con.ValueArgs.Count;
        var typeIndex = 0;
        var arguments = new List<string>(argumentCount);
        for (var parameterIndex = 0; parameterIndex < argumentCount; parameterIndex++)
        {
            if (valueArguments.TryGetValue(parameterIndex, out var valueArgument))
            {
                arguments.Add(valueArgument.ValueVariableIndex >= 0
                    ? $"vv:{valueArgument.ValueVariableIndex}"
                    : $"v:{valueArgument.CanonicalHash}");
            }
            else if (typeIndex < con.Args.Count)
            {
                arguments.Add($"t:{TypeToString(con.Args[typeIndex++])}");
            }
        }

        return $"{con.Name}<{string.Join(",", arguments)}>";
    }
}
