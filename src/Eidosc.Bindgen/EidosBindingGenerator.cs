using System.Text;
using Eidosc.Bindgen.Models;

namespace Eidosc.Bindgen;

public sealed class EidosBindingGenerator
{
    private readonly CHeaderIr _ir;
    private readonly string _libraryName;

    public EidosBindingGenerator(CHeaderIr ir, string libraryName)
    {
        _ir = ir;
        _libraryName = libraryName;
        TypeMapper.RegisterStructNames(ir.Structs);
    }

    public string Generate()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"// Auto-generated Eidos FFI bindings for {_libraryName}");
        sb.AppendLine($"// Source: {_ir.Header}");
        sb.AppendLine($"// Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        // Module declaration
        sb.AppendLine($"module {_libraryName}_ffi");
        sb.AppendLine();
        sb.AppendLine($"link \"{_libraryName}\"");
        sb.AppendLine();

        // Enums → val constants
        GenerateEnums(sb);

        // Structs → @cstruct declarations
        GenerateStructs(sb);

        // Functions → @ffi declarations
        GenerateFunctions(sb);

        return sb.ToString();
    }

    private void GenerateEnums(StringBuilder sb)
    {
        if (_ir.Enums.Count == 0) return;

        sb.AppendLine("// === Enums ===");
        sb.AppendLine();

        foreach (var en in _ir.Enums)
        {
            sb.AppendLine($"// {en.EffectiveName}");

            foreach (var v in en.Values)
            {
                var eidosName = TypeMapper.EidosFunctionName(v.Name);
                sb.AppendLine($"val {eidosName}: Int = {v.Value}");
            }

            sb.AppendLine();
        }
    }

    private void GenerateStructs(StringBuilder sb)
    {
        if (_ir.Structs.Count == 0) return;

        sb.AppendLine("// === Structs ===");
        sb.AppendLine();

        foreach (var st in _ir.Structs)
        {
            var name = st.EffectiveName;

            sb.AppendLine($"// {name} (size: {st.Size}, alignment: {st.Alignment})");
            sb.AppendLine("@cstruct");
            sb.AppendLine($"type {name} = struct {{");

            foreach (var field in st.Fields)
            {
                var mapping = TypeMapper.MapCType(field.Type);
                var eidosField = ToEidosFieldName(field.Name);
                sb.AppendLine($"    {eidosField}: {mapping.EidosType},  // offset: {field.Offset}, size: {field.Size}");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }
    }

    private void GenerateFunctions(StringBuilder sb)
    {
        if (_ir.Functions.Count == 0) return;

        sb.AppendLine("// === Functions ===");
        sb.AppendLine();

        int directCount = 0, shimCount = 0, skipCount = 0;

        foreach (var fn in _ir.Functions)
        {
            var (canBind, reason) = CanBindFunction(fn);

            if (!canBind)
            {
                sb.AppendLine($"// SKIP: {fn.Name} — {reason}");
                skipCount++;
                continue;
            }

            bool needsShim = NeedsShimFunction(fn);

            if (needsShim)
            {
                GenerateShimmedFunction(sb, fn);
                shimCount++;
            }
            else
            {
                GenerateDirectFunction(sb, fn);
                directCount++;
            }
        }

        sb.AppendLine($"// Summary: {directCount} direct, {shimCount} shim, {skipCount} skipped");
    }

    private void GenerateDirectFunction(StringBuilder sb, CFunctionInfo fn)
    {
        var eidosName = TypeMapper.EidosFunctionName(fn.Name);
        var retMapping = TypeMapper.MapCType(fn.ReturnType);

        sb.Append($"@ffi(\"{fn.Name}\")");
        sb.Append($" func {eidosName}: ");

        if (fn.Params.Count == 0)
        {
            sb.Append($"Unit -> {retMapping.EidosType}");
        }
        else
        {
            for (int i = 0; i < fn.Params.Count; i++)
            {
                var paramMapping = TypeMapper.MapCType(fn.Params[i].Type);
                sb.Append($"{paramMapping.EidosType} -> ");
            }
            sb.Append(retMapping.EidosType);
        }

        sb.AppendLine();
    }

    private void GenerateShimmedFunction(StringBuilder sb, CFunctionInfo fn)
    {
        var eidosName = TypeMapper.EidosFunctionName(fn.Name);
        var retMapping = TypeMapper.MapCType(fn.ReturnType);

        sb.Append($"// Needs C shim (struct-by-value params)");
        sb.AppendLine();
        sb.Append($"// @ffi(\"eidos_shim_{fn.Name}\")");
        sb.Append($" func {eidosName}: ");

        if (fn.Params.Count == 0)
        {
            sb.Append($"Unit -> {retMapping.EidosType}");
        }
        else
        {
            for (int i = 0; i < fn.Params.Count; i++)
            {
                var paramMapping = TypeMapper.MapCType(fn.Params[i].Type);
                var shimType = paramMapping.Category == EidosTypeCategory.StructByValue
                    ? "RawPtr" : paramMapping.EidosType;
                sb.Append($"{shimType} -> ");
            }
            sb.Append(retMapping.EidosType);
        }

        sb.AppendLine();
    }

    private static (bool canBind, string reason) CanBindFunction(CFunctionInfo fn)
    {
        if (fn.IsVariadic)
            return (false, "variadic function");

        // Check return type
        var retMapping = TypeMapper.MapCType(fn.ReturnType);
        if (retMapping.Category == EidosTypeCategory.Unsupported)
            return (false, $"unsupported return type: {fn.ReturnType.Spelling}");

        // Check parameters
        foreach (var param in fn.Params)
        {
            var mapping = TypeMapper.MapCType(param.Type);
            if (mapping.Category == EidosTypeCategory.Unsupported)
                return (false, $"unsupported param type: {param.Type.Spelling}");
        }

        return (true, "");
    }

    private static bool NeedsShimFunction(CFunctionInfo fn)
    {
        // Check if any parameter or return type is struct-by-value
        var retMapping = TypeMapper.MapCType(fn.ReturnType);
        if (retMapping.Category == EidosTypeCategory.StructByValue)
            return true;

        foreach (var param in fn.Params)
        {
            var mapping = TypeMapper.MapCType(param.Type);
            if (mapping.Category == EidosTypeCategory.StructByValue)
                return true;
        }

        return false;
    }

    private static string ToEidosFieldName(string cName)
    {
        // Eidos field names should be lowercase with underscores
        if (string.IsNullOrEmpty(cName)) return cName;

        var sb = new StringBuilder();
        for (int i = 0; i < cName.Length; i++)
        {
            char c = cName[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
