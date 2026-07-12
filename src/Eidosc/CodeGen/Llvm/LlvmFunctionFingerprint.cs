using System.Security.Cryptography;
using System.Text;
using Eidosc.Pipeline;

namespace Eidosc.CodeGen.Llvm;

public sealed record LlvmFunctionFingerprint(
    string FunctionKey,
    string BodyHash,
    int BasicBlockCount,
    int InstructionCount,
    int ParameterCount);

public sealed record LlvmFunctionFragment(
    string FunctionKey,
    string BodyHash,
    string IrFragment,
    string DeclarationIr,
    string Linkage,
    int BasicBlockCount,
    int InstructionCount,
    int ParameterCount);

public sealed record LlvmFunctionFingerprintSnapshot(
    string SchemaVersion,
    IReadOnlyList<LlvmFunctionFingerprint> Functions)
{
    public const string CurrentSchemaVersion = "llvm-function-fingerprint-snapshot-v1";

    public static LlvmFunctionFingerprintSnapshot FromModule(LlvmModule module) =>
        new(
            CurrentSchemaVersion,
            LlvmFunctionFingerprintBuilder.ComputeModule(module));

    public string ModuleFingerprint => ModuleArtifactHash.ComputeJsonHash(Functions);
}

public sealed record LlvmFunctionFragmentSnapshot(
    string SchemaVersion,
    IReadOnlyList<LlvmFunctionFragment> Functions)
{
    public const string CurrentSchemaVersion = "llvm-function-fragment-snapshot-v1";

    public static LlvmFunctionFragmentSnapshot FromModule(LlvmModule module) =>
        new(
            CurrentSchemaVersion,
            LlvmFunctionFingerprintBuilder.BuildModuleFragments(module));

    public string ModuleFingerprint => ModuleArtifactHash.ComputeJsonHash(
            Functions.Select(static fragment => new
            {
                fragment.FunctionKey,
                fragment.BodyHash,
                fragment.DeclarationIr,
                fragment.Linkage,
                fragment.BasicBlockCount,
                fragment.InstructionCount,
                fragment.ParameterCount
            }).ToArray());
}

public sealed record LlvmRecomposedModuleSnapshot(
    string SchemaVersion,
    string IrText,
    string EnvelopeFingerprint,
    string FunctionFragmentFingerprint,
    int FunctionCount);

public sealed record LlvmRecomposedObjectGroupSnapshot(
    string SchemaVersion,
    string GroupKey,
    string RootFunctionKey,
    string IrText,
    int FunctionCount,
    int IrBytes);

public static class LlvmFunctionFingerprintBuilder
{
    private const string Schema = "llvm-function-fingerprint-v1";

    public static LlvmFunctionFingerprint Compute(LlvmFunction function)
    {
        var fragment = BuildFragment(function);
        return new LlvmFunctionFingerprint(
            fragment.FunctionKey,
            fragment.BodyHash,
            fragment.BasicBlockCount,
            fragment.InstructionCount,
            fragment.ParameterCount);
    }

    public static IReadOnlyList<LlvmFunctionFingerprint> ComputeModule(LlvmModule module)
    {
        return module.Functions
            .Select(Compute)
            .OrderBy(static fingerprint => fingerprint.FunctionKey, StringComparer.Ordinal)
            .ToArray();
    }

    public static LlvmFunctionFragment BuildFragment(LlvmFunction function)
    {
        var fragment = BuildFunctionFragment(function);
        var bodyHash = ComputeHash($"{Schema}\0{fragment}");
        return new LlvmFunctionFragment(
            GetFunctionKey(function),
            bodyHash,
            fragment,
            BuildFunctionDeclaration(function),
            function.Linkage.ToString(),
            function.BasicBlocks.Count,
            function.BasicBlocks.Sum(static block => block.Instructions.Count),
            function.Parameters.Count);
    }

    public static IReadOnlyList<LlvmFunctionFragment> BuildModuleFragments(LlvmModule module)
    {
        return module.Functions
            .Select(BuildFragment)
            .OrderBy(static fragment => fragment.FunctionKey, StringComparer.Ordinal)
            .ToArray();
    }

    public static LlvmRecomposedModuleSnapshot RecomposeModule(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmFunctionFragmentSnapshot functions)
    {
        return RecomposeModule(
            envelope,
            functions,
            functions.Functions
                .Select(static fragment => fragment.FunctionKey)
                .OrderBy(static key => key, StringComparer.Ordinal)
                .ToArray());
    }

    public static LlvmRecomposedModuleSnapshot RecomposeModule(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmFunctionFragmentSnapshot functions,
        IReadOnlyList<string> functionOrder)
    {
        var sb = new StringBuilder();
        AppendLines(sb, envelope.HeaderIr);
        sb.AppendLine();
        AppendLines(sb, envelope.TypeDefinitionIr);
        AppendLines(sb, envelope.GlobalIr);
        AppendLines(sb, envelope.DeclarationIr);
        AppendLines(sb, envelope.AttributeGroupIr);
        var fragmentByKey = functions.Functions.ToDictionary(static fragment => fragment.FunctionKey, StringComparer.Ordinal);
        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in functionOrder)
        {
            if (!emittedKeys.Add(key) ||
                !fragmentByKey.TryGetValue(key, out var function))
            {
                continue;
            }

            sb.AppendLine();
            sb.Append(function.IrFragment);
        }

        foreach (var function in functions.Functions
                     .Where(fragment => !emittedKeys.Contains(fragment.FunctionKey))
                     .OrderBy(static fragment => fragment.FunctionKey, StringComparer.Ordinal))
        {
            sb.AppendLine();
            sb.Append(function.IrFragment);
        }

        return new LlvmRecomposedModuleSnapshot(
            "llvm-recomposed-module-snapshot-v1",
            sb.ToString(),
            envelope.EnvelopeFingerprint,
            functions.ModuleFingerprint,
            functions.Functions.Count);
    }

    public static LlvmRecomposedObjectGroupSnapshot RecomposeObjectGroup(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmFunctionFragmentSnapshot functions,
        LlvmCodegenUnitPlanObjectGroup group)
    {
        var fragmentByKey = functions.Functions.ToDictionary(static fragment => fragment.FunctionKey, StringComparer.Ordinal);
        var memberKeys = group.MemberFunctionKeys.ToHashSet(StringComparer.Ordinal);
        var referencedSymbols = group.ReferencedSymbols.ToHashSet(StringComparer.Ordinal);
        var referencedTypeNames = group.ReferencedTypeNames.ToHashSet(StringComparer.Ordinal);
        var sb = new StringBuilder();
        AppendLines(sb, envelope.HeaderIr);
        sb.AppendLine();
        AppendLines(sb, envelope.GetObjectGroupTypeDefinitionIr(referencedTypeNames));
        AppendLines(sb, envelope.GetObjectGroupGlobalIr(referencedSymbols));
        AppendLines(sb, envelope.GetObjectGroupDeclarationIr(referencedSymbols));
        foreach (var declaration in functions.Functions
                     .Where(fragment =>
                         !memberKeys.Contains(fragment.FunctionKey) &&
                         !IsLocalToObject(fragment.Linkage) &&
                         referencedSymbols.Contains(FunctionNameFromKey(fragment.FunctionKey)))
                     .OrderBy(static fragment => fragment.FunctionKey, StringComparer.Ordinal)
                     .Select(static fragment => fragment.DeclarationIr)
                     .Where(static declaration => declaration.Length > 0))
        {
            sb.AppendLine(declaration);
        }

        AppendLines(sb, envelope.AttributeGroupIr);
        foreach (var memberKey in group.MemberFunctionKeys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            if (!fragmentByKey.TryGetValue(memberKey, out var fragment))
            {
                continue;
            }

            sb.AppendLine();
            sb.Append(fragment.IrFragment);
        }

        var ir = sb.ToString();
        return new LlvmRecomposedObjectGroupSnapshot(
            "llvm-recomposed-object-group-snapshot-v1",
            group.GroupKey,
            group.RootFunctionKey,
            ir,
            group.MemberFunctionKeys.Count,
            ir.Length);
    }

    public static string BuildFunctionFragment(LlvmFunction function)
    {
        var sb = new StringBuilder();
        AppendFunction(sb, function);
        return sb.ToString();
    }

    private static string BuildFunctionDeclaration(LlvmFunction function)
    {
        if (function.IsDeclaration || function.BasicBlocks.Count == 0)
        {
            return "";
        }

        var returnType = function.ReturnType.ToIrString();
        var parameters = string.Join(", ", function.Parameters.Select(static parameter => parameter.Type.ToIrString()));
        var attrs = function.AttributeIds.Count > 0 ? $" #{function.AttributeIds[0]}" : "";
        return $"declare {returnType} @{function.Name}({parameters}){attrs}";
    }

    private static void AppendFunction(StringBuilder sb, LlvmFunction function)
    {
        var linkage = function.Linkage.ToIrString();
        var returnType = function.ReturnType.ToIrString();
        var parameters = string.Join(", ", function.Parameters.Select(FormatParameter));
        var attrs = function.AttributeIds.Count > 0 ? $" #{function.AttributeIds[0]}" : "";

        if (function.BasicBlocks.Count == 0)
        {
            sb.Append("declare ")
                .Append(returnType)
                .Append(" @")
                .Append(function.Name)
                .Append('(')
                .Append(parameters)
                .Append(')')
                .Append(attrs)
                .AppendLine();
            return;
        }

        sb.Append("define ")
            .Append(linkage)
            .Append(' ')
            .Append(returnType)
            .Append(" @")
            .Append(function.Name)
            .Append('(')
            .Append(parameters)
            .Append(')')
            .Append(attrs)
            .AppendLine(" {");

        foreach (var block in function.BasicBlocks)
        {
            AppendBasicBlock(sb, block);
        }

        sb.AppendLine("}");
    }

    private static void AppendBasicBlock(StringBuilder sb, LlvmBasicBlock block)
    {
        sb.Append("  ").Append(block.Label).AppendLine(":");
        foreach (var phi in block.Instructions.OfType<LlvmPhi>())
        {
            sb.Append("    ").AppendLine(phi.ToIrString());
        }

        foreach (var instruction in block.Instructions.Where(static instruction => instruction is not LlvmPhi))
        {
            sb.Append("    ").AppendLine(instruction.ToIrString());
        }

        if (block.Terminator != null)
        {
            sb.Append("    ").AppendLine(block.Terminator.ToIrString());
        }
    }

    private static string FormatParameter(LlvmParameter parameter)
    {
        var name = string.IsNullOrEmpty(parameter.Name) ? "" : $" %{parameter.Name}";
        return $"{parameter.Type.ToIrString()}{name}";
    }

    private static string GetFunctionKey(LlvmFunction function) => string.IsNullOrWhiteSpace(function.Name)
        ? "anon:<unknown>"
        : $"name:{function.Name}";

    private static string FunctionNameFromKey(string functionKey) =>
        functionKey.StartsWith("name:", StringComparison.Ordinal)
            ? functionKey["name:".Length..]
            : functionKey;

    private static bool IsLocalToObject(string linkage) =>
        string.Equals(linkage, LlvmLinkage.Private.ToString(), StringComparison.Ordinal) ||
        string.Equals(linkage, LlvmLinkage.Internal.ToString(), StringComparison.Ordinal);

    private static void AppendLines(StringBuilder sb, IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            sb.AppendLine(line);
        }
    }

    private static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
