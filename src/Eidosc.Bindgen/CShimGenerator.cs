using System.Text;
using Eidosc.Bindgen.Models;

namespace Eidosc.Bindgen;

public sealed class CShimGenerator
{
    private readonly CHeaderIr _ir;
    private readonly string _libraryName;

    public CShimGenerator(CHeaderIr ir, string libraryName)
    {
        _ir = ir;
        _libraryName = libraryName;
        TypeMapper.RegisterStructNames(ir.Structs);
    }

    public string Generate()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"// Auto-generated C shim for {_libraryName} FFI bindings");
        sb.AppendLine($"// Source: {_ir.Header}");
        sb.AppendLine($"// Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine($"#include \"{Path.GetFileName(_ir.Header)}\"");
        sb.AppendLine();

        int count = 0;

        foreach (var fn in _ir.Functions)
        {
            if (!NeedsShim(fn)) continue;

            GenerateShimFunction(sb, fn);
            count++;
        }

        if (count == 0)
        {
            sb.AppendLine("// No shim functions needed — all functions use FFI-safe types directly.");
        }
        else
        {
            sb.Insert(sb.Length, "");
        }

        return sb.ToString();
    }

    public bool HasShims()
    {
        return _ir.Functions.Any(NeedsShim);
    }

    private static bool NeedsShim(CFunctionInfo fn)
    {
        if (fn.IsVariadic) return false;

        var retMapping = TypeMapper.MapCType(fn.ReturnType);
        if (retMapping.Category == EidosTypeCategory.StructByValue) return true;

        return fn.Params.Any(p =>
            TypeMapper.MapCType(p.Type).Category == EidosTypeCategory.StructByValue);
    }

    private void GenerateShimFunction(StringBuilder sb, CFunctionInfo fn)
    {
        var retMapping = TypeMapper.MapCType(fn.ReturnType);
        var retType = retMapping.Category == EidosTypeCategory.StructByValue
            ? "void*" : fn.ReturnType.Spelling;

        sb.Append($"{retType} eidos_shim_{fn.Name}(");

        var paramList = new List<string>();
        for (int i = 0; i < fn.Params.Count; i++)
        {
            var mapping = TypeMapper.MapCType(fn.Params[i].Type);
            if (mapping.Category == EidosTypeCategory.StructByValue)
            {
                paramList.Add($"void *{fn.Params[i].Name}_ptr");
            }
            else
            {
                paramList.Add($"{fn.Params[i].Type.Spelling} {fn.Params[i].Name}");
            }
        }

        sb.Append(string.Join(", ", paramList));
        sb.Append(") {");

        if (retMapping.Category == EidosTypeCategory.StructByValue)
        {
            sb.AppendLine();
            sb.Append($"    {fn.Name}_result = {fn.Name}(");
            sb.Append(string.Join(", ", fn.Params.Select((p, i) =>
            {
                var m = TypeMapper.MapCType(p.Type);
                return m.Category == EidosTypeCategory.StructByValue
                    ? $"*({p.Type.Spelling}*){p.Name}_ptr" : p.Name;
            })));
            sb.AppendLine(");");
            // Would need to allocate and return pointer to result
            sb.AppendLine("    // TODO: return struct-by-value via pointer");
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
        }
        else
        {
            sb.Append($" return {fn.Name}(");
            sb.Append(string.Join(", ", fn.Params.Select((p, i) =>
            {
                var m = TypeMapper.MapCType(p.Type);
                return m.Category == EidosTypeCategory.StructByValue
                    ? $"*({p.Type.Spelling}*){p.Name}_ptr" : p.Name;
            })));
            sb.AppendLine("); }");
        }

        sb.AppendLine();
    }
}
