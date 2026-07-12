using System.Text;

namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// LLVM IR 文本发射器 - 将 LlvmModule 转换为 LLVM IR 文本
/// </summary>
public sealed class LlvmEmitter
{
    private const string DefaultDataLayout = "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128";
    private const string DefaultTargetTriple = "x86_64-pc-linux-gnu";

    private readonly StringBuilder _sb = new();
    private int _indent = 0;
    private readonly List<LlvmStructType> _typeDefinitions = [];

    /// <summary>
    /// 发射 LLVM 模块为 IR 文本
    /// </summary>
    public string Emit(LlvmModule module)
    {
        return Emit(module, dataLayout: null, targetTriple: null);
    }

    /// <summary>
    /// 发射 LLVM 模块为 IR 文本（可指定目标信息）
    /// </summary>
    public string Emit(LlvmModule module, string? dataLayout, string? targetTriple)
    {
        _sb.Clear();
        _indent = 0;
        _typeDefinitions.Clear();

        // 收集类型定义
        CollectTypeDefinitions(module);

        // 文件头
        EmitLine($"; ModuleID = '{module.Name}'");
        EmitLine($"source_filename = \"{module.Name}\"");
        EmitLine();

        // 目标信息
        var effectiveDataLayout = string.IsNullOrWhiteSpace(dataLayout) ? DefaultDataLayout : dataLayout;
        var effectiveTargetTriple = string.IsNullOrWhiteSpace(targetTriple) ? DefaultTargetTriple : targetTriple;
        EmitLine($"target datalayout = \"{effectiveDataLayout}\"");
        EmitLine($"target triple = \"{effectiveTargetTriple}\"");
        EmitLine();

        // 类型定义
        foreach (var typeDef in _typeDefinitions)
        {
            EmitTypeDefinition(typeDef);
        }

        // 全局变量
        foreach (var global in module.Globals)
        {
            EmitGlobal(global);
        }

        // 外部声明
        foreach (var decl in module.Declarations)
        {
            EmitDeclaration(decl);
        }

        // 属性组
        foreach (var attrGroup in module.AttributeGroups)
        {
            EmitAttributeGroup(attrGroup);
        }

        // 函数
        foreach (var func in module.Functions)
        {
            EmitFunction(func);
        }

        return _sb.ToString();
    }

    /// <summary>
    /// 收集模块中使用的类型定义
    /// </summary>
    private void CollectTypeDefinitions(LlvmModule module)
    {
        // 从模块预注册的具名结构体类型收集
        foreach (var namedStruct in module.NamedStructTypes)
        {
            CollectTypesFromType(namedStruct);
        }

        // 从全局变量收集
        foreach (var global in module.Globals)
        {
            CollectTypesFromType(global.Type);
        }

        // 从函数收集
        foreach (var func in module.Functions)
        {
            CollectTypesFromType(func.ReturnType);
            foreach (var param in func.Parameters)
            {
                CollectTypesFromType(param.Type);
            }
            foreach (var block in func.BasicBlocks)
            {
                foreach (var instr in block.Instructions)
                {
                    if (instr is LlvmGetElementPtr { StructType: { } gepStructType })
                    {
                        CollectTypesFromType(gepStructType);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 从类型中收集结构体定义
    /// </summary>
    private void CollectTypesFromType(LlvmType type)
    {
        if (type is LlvmStructType structType && !string.IsNullOrEmpty(structType.Name))
        {
            if (!_typeDefinitions.Contains(structType))
            {
                _typeDefinitions.Add(structType);
                foreach (var field in structType.Fields)
                {
                    CollectTypesFromType(field);
                }
            }
        }
    }

    /// <summary>
    /// 发射类型定义
    /// </summary>
    private void EmitTypeDefinition(LlvmStructType type)
    {
        if (string.IsNullOrEmpty(type.Name))
            return;

        var fields = string.Join(", ", type.Fields.Select(f => f.ToIrString()));
        EmitLine($"%struct.{type.Name} = type {{ {fields} }}");
    }

    /// <summary>
    /// 发射全局变量
    /// </summary>
    private void EmitGlobal(LlvmGlobal global)
    {
        var linkage = global.Linkage == LlvmLinkage.External ? "" : $"{global.Linkage.ToIrString()} ";
        var type = global.Type.ToIrString();
        var name = $"@{global.Name}";
        var storageClass = global.IsConstant ? "constant" : "global";
        var initializer = global.Initializer != null
            ? $" {global.Initializer.ToIrString()}"
            : " zeroinitializer";

        EmitLine($"{name} = {linkage}{storageClass} {type}{initializer}");
    }

    /// <summary>
    /// 发射外部声明
    /// </summary>
    private void EmitDeclaration(LlvmDeclaration decl)
    {
        var name = $"@{decl.Name}";
        if (decl.Type is LlvmFunctionType functionType)
        {
            var returnType = functionType.ReturnType.ToIrString();
            var parameters = string.Join(", ", functionType.ParameterTypes.Select(param => param.ToIrString()));
            if (functionType.IsVarArg)
            {
                parameters = string.IsNullOrEmpty(parameters) ? "..." : $"{parameters}, ...";
            }

            EmitLine($"declare {returnType} {name}({parameters})");
            return;
        }

        var type = decl.Type.ToIrString();
        EmitLine($"declare {type} {name}");
    }

    /// <summary>
    /// 发射属性组
    /// </summary>
    private void EmitAttributeGroup(LlvmAttributeGroup attrGroup)
    {
        EmitLine($"attributes #{attrGroup.Id} = {{ {attrGroup.Attributes} }}");
    }

    /// <summary>
    /// 发射函数
    /// </summary>
    private void EmitFunction(LlvmFunction func)
    {
        EmitLine();

        // 函数签名
        var linkage = func.Linkage.ToIrString();
        var returnType = func.ReturnType.ToIrString();
        var name = $"@{func.Name}";
        var parameters = string.Join(", ", func.Parameters.Select(EmitParameter));

        // 函数属性
        var attrs = "";
        if (func.AttributeIds.Count > 0)
        {
            attrs = $" #{func.AttributeIds[0]}";
        }

        if (func.BasicBlocks.Count == 0)
        {
            // 声明
            EmitLine($"declare {returnType} {name}({parameters}){attrs}");
        }
        else
        {
            // 定义
            EmitLine($"define {linkage} {returnType} {name}({parameters}){attrs} {{");
            _indent++;

            // 基本块
            foreach (var block in func.BasicBlocks)
            {
                EmitBasicBlock(block);
            }

            _indent--;
            EmitLine("}");
        }
    }

    /// <summary>
    /// 发射参数
    /// </summary>
    private static string EmitParameter(LlvmParameter param)
    {
        var type = param.Type.ToIrString();
        var name = string.IsNullOrEmpty(param.Name) ? "" : $" %{param.Name}";
        return $"{type}{name}";
    }

    /// <summary>
    /// 发射基本块
    /// </summary>
    private void EmitBasicBlock(LlvmBasicBlock block)
    {
        // 块标签
        EmitLine($"{block.Label}:");

        // PHI 指令（必须在块的开头）
        foreach (var phi in block.Instructions.OfType<LlvmPhi>())
        {
            EmitInstruction(phi);
        }

        // 普通指令（跳过 PHI）
        foreach (var instr in block.Instructions.Where(i => i is not LlvmPhi))
        {
            EmitInstruction(instr);
        }

        // 终止指令
        if (block.Terminator != null)
        {
            EmitTerminator(block.Terminator);
        }
    }

    /// <summary>
    /// 发射指令
    /// </summary>
    private void EmitInstruction(LlvmInstruction instr)
    {
        var instrStr = instr.ToIrString();
        EmitLine($"  {instrStr}");
    }

    /// <summary>
    /// 发射终止指令
    /// </summary>
    private void EmitTerminator(LlvmTerminator term)
    {
        EmitLine($"  {term.ToIrString()}");
    }

    #region 辅助方法

    private void EmitLine(string? line = null)
    {
        if (line != null)
        {
            _sb.Append(new string(' ', _indent * 2));
            _sb.Append(line);
        }
        _sb.AppendLine();
    }

    #endregion
}

/// <summary>
/// 链接类型扩展
/// </summary>
public static class LlvmLinkageExtensions
{
    public static string ToIrString(this LlvmLinkage linkage)
    {
        return linkage switch
        {
            LlvmLinkage.Private => "private",
            LlvmLinkage.Internal => "internal",
            LlvmLinkage.External => "external",
            LlvmLinkage.LinkOnce => "linkonce",
            LlvmLinkage.Weak => "weak",
            LlvmLinkage.Common => "common",
            LlvmLinkage.LinkOnceOdr => "linkonce_odr",
            LlvmLinkage.WeakOdr => "weak_odr",
            _ => "external"
        };
    }
}
