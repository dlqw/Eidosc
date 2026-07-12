using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Ast.Declarations;
using System.Text;
using Eidosc.Ast;
using Eidosc.Pipeline;
using Eidosc.Semantic;

namespace Eidosc.Types;

/// <summary>
/// Formatting utilities extracted from PhaseOutput (A3).
/// </summary>
public static class TypeFormatter
{
    public static string FormatInferredTypes(ModuleDecl module, TypeInferer inferer)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.InferredTypesHeader);
        sb.AppendLine();
        FormatNodeTypes(module, 0, sb);
        return sb.ToString();
    }


    private static void AppendIndentedBlock(StringBuilder sb, string label, string content)
    {
        sb.AppendLine(label);
        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            sb.Append("  ");
            sb.AppendLine(line);
        }
    }


    private static void FormatNodeTypes(EidosAstNode node, int indent, StringBuilder sb)
    {
        var prefix = new string(' ', indent * 2);

        if (node.InferredType != null)
        {
            var name = node.GetType().Name;
            var details = MirFormatter.GetAstNodeDetails(node);
            sb.AppendLine($"{prefix}{name}: {node.InferredType} // {details}");
        }

        foreach (var child in MirFormatter.GetAstChildren(node))
        {
            FormatNodeTypes(child, indent + 1, sb);
        }
    }

    /// <summary>
    /// 格式化编译摘要
    /// </summary>


    public static string FormatSubstitution(Substitution substitution, ModuleDecl? module = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.SubstitutionHeader);

        var bindings = substitution.GetBindingsSnapshot();
        var bindingByIndex = bindings.ToDictionary(binding => binding.TypeVarIndex);
        var contexts = module != null
            ? CollectTypeVarContexts(module)
            : new Dictionary<int, List<string>>();

        var allIndices = bindingByIndex.Keys
            .Concat(contexts.Keys)
            .Distinct()
            .OrderBy(index => index)
            .ToList();

        sb.AppendLine(PipelineMessages.BindingCount(bindings.Count));
        sb.AppendLine($"// FreshVarNext: 't{substitution.NextFreshVarIndex}");

        if (allIndices.Count == 0)
        {
            sb.AppendLine(PipelineMessages.NoSubstitutionBindings);
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine($"{PipelineMessages.TypeVariableColumn,-10} | {"Raw",-26} | {"Resolved",-26} | {PipelineMessages.StatusColumn,-10}");
        sb.AppendLine(Separator('-'));

        foreach (var index in allIndices)
        {
            if (bindingByIndex.TryGetValue(index, out var binding))
            {
                var raw = TrimForColumn(binding.RawType.ToString(), 26);
                var resolved = TrimForColumn(binding.ResolvedType.ToString(), 26);
                var status = raw == resolved ? "stable" : "rewritten";
                sb.AppendLine($"'t{index,-8} | {raw,-26} | {resolved,-26} | {status,-10}");

                if (binding.Chain.Contains(WellKnownStrings.Punctuation.FatArrow, StringComparison.Ordinal))
                {
                    sb.AppendLine($"//   chain: {binding.Chain}");
                }
            }
            else
            {
                sb.AppendLine($"'t{index,-8} | {"(unbound)",-26} | {"(unbound)",-26} | {"unbound",-10}");
            }

            if (contexts.TryGetValue(index, out var usageContexts) && usageContexts.Count > 0)
            {
                foreach (var context in usageContexts.Take(3))
                {
                    sb.AppendLine($"//   context: {context}");
                }

                if (usageContexts.Count > 3)
                {
                    sb.AppendLine($"//   context: ... (+{usageContexts.Count - 3} more)");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化能力推断结果
    /// </summary>


    private static Dictionary<int, List<string>> CollectTypeVarContexts(ModuleDecl module)
    {
        var contextsByVar = new Dictionary<int, List<string>>();
        CollectTypeVarContextsFromNode(module, contextsByVar);
        return contextsByVar;
    }

    internal static void CollectTypeVarContextsFromNode(
        EidosAstNode node,
        Dictionary<int, List<string>> contextsByVar)
    {
        if (node.InferredType is Eidosc.Types.Type type)
        {
            var indices = CollectTypeVarIndices(type);
            if (indices.Count > 0)
            {
                var line = node.Span.Location.Line + 1;
                var col = node.Span.Location.Column + 1;
                var details = MirFormatter.GetAstNodeDetails(node);
                var detailsPart = string.IsNullOrEmpty(details)
                    ? ""
                    : $" // {TrimForColumn(details, 60)}";
                var context = $"{node.GetType().Name} @ {line}:{col}{detailsPart}";

                foreach (var index in indices)
                {
                    if (!contextsByVar.TryGetValue(index, out var list))
                    {
                        list = [];
                        contextsByVar[index] = list;
                    }

                    if (!list.Contains(context))
                    {
                        list.Add(context);
                    }
                }
            }
        }

        foreach (var child in MirFormatter.GetAstChildren(node))
        {
            CollectTypeVarContextsFromNode(child, contextsByVar);
        }
    }

    private static HashSet<int> CollectTypeVarIndices(Eidosc.Types.Type type)
    {
        var indices = new HashSet<int>();
        CollectTypeVarIndices(type, indices);
        return indices;
    }


    private static void CollectTypeVarIndices(Eidosc.Types.Type type, HashSet<int> indices)
    {
        switch (type)
        {
            case TyVar var:
                indices.Add(var.Index);
                if (var.Instance != null)
                {
                    CollectTypeVarIndices(var.Instance, indices);
                }
                break;

            case TyCon con:
                foreach (var arg in con.Args)
                {
                    CollectTypeVarIndices(arg, indices);
                }
                break;

            case TyFun fun:
                foreach (var param in fun.Params)
                {
                    CollectTypeVarIndices(param, indices);
                }
                CollectTypeVarIndices(fun.Result, indices);
                foreach (var freeVar in fun.Effects.FreeTypeVariables())
                {
                    indices.Add(freeVar);
                }
                break;

            case TyTuple tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectTypeVarIndices(element, indices);
                }
                break;

            case TyRef reference:
                CollectTypeVarIndices(reference.Inner, indices);
                break;

            case TyMutRef mutReference:
                CollectTypeVarIndices(mutReference.Inner, indices);
                break;
            case TyShared shared:
                CollectTypeVarIndices(shared.Inner, indices);
                break;
        }
    }



    /// <summary>
    /// 格式化 LLVM IR 模块
    /// </summary>


    public static string FormatAbilities(ModuleDecl module, EffectInferer inferer)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.AbilitiesHeader);
        sb.AppendLine();

        if (inferer.FunctionSummaries.Count == 0)
        {
            sb.AppendLine(PipelineMessages.NoEffectRequirements);
            return sb.ToString();
        }

        sb.AppendLine(PipelineMessages.FunctionEffectRequirementsHeader);
        sb.AppendLine(Debug.DebugFormatter.Separator('-'));
        sb.AppendLine($"{PipelineMessages.FunctionNameColumn,-30} | {PipelineMessages.EffectRequirementsColumn}");
        sb.AppendLine(Debug.DebugFormatter.Separator('-'));

        foreach (var (funcDef, summary) in inferer.FunctionSummaries)
        {
            var declared = summary.DeclaredUpperBound.IsPure ? PipelineMessages.PureEffect : summary.DeclaredUpperBound.ToString();
            var inferred = summary.InferredEffects.IsPure ? PipelineMessages.PureEffect : summary.InferredEffects.ToString();
            sb.AppendLine($"{funcDef.Name,-30} | declared={declared}; inferred={inferred}");
        }

        return sb.ToString();
    }

    private static string Separator(char c) => new string(c, 80);

    private static string TrimForColumn(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 2)] + WellKnownStrings.Punctuation.DotDot;
    }
}
