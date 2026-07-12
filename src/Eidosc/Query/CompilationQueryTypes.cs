using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Borrow;
using Eidosc.Hir;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Query;

public interface ICompilationQueryOutput
{
    bool IsIncomplete { get; }
    string? IncompleteReason { get; }
}

public sealed class ParseOutput : ICompilationQueryOutput
{
    public bool IsIncomplete { get; init; }
    public string? IncompleteReason { get; init; }
    public ModuleDecl? Ast { get; init; }
    public required List<Token> Tokens { get; init; }
}

public sealed class NameResolutionOutput : ICompilationQueryOutput
{
    public bool IsIncomplete { get; init; }
    public string? IncompleteReason { get; init; }
    public SymbolTable? SymbolTable { get; init; }
    public NameResolver? NameResolver { get; init; }
}

public sealed class TypeInferenceOutput : ICompilationQueryOutput
{
    public bool IsIncomplete { get; init; }
    public string? IncompleteReason { get; init; }
    public TypeInferer? TypeInferer { get; init; }
    public EffectInferer? EffectInferer { get; init; }
}

public sealed class EffectInferenceOutput : ICompilationQueryOutput
{
    public bool IsIncomplete { get; init; }
    public string? IncompleteReason { get; init; }
    public EffectInferer? EffectInferer { get; init; }
}

public sealed class HirOutput : ICompilationQueryOutput
{
    public bool IsIncomplete { get; init; }
    public string? IncompleteReason { get; init; }
    public HirModule? HirModule { get; init; }
    public IReadOnlySet<TypeId> CopyLikeTypeIds { get; init; } = new HashSet<TypeId>();
    public IReadOnlyDictionary<TypeId, string> DynamicTypeKeys { get; init; } = new Dictionary<TypeId, string>();
    public IReadOnlyDictionary<int, TypeDescriptor> TypeDescriptors { get; init; } = new Dictionary<int, TypeDescriptor>();
    public IReadOnlyDictionary<int, List<ConstructorTypeLayout>> ConstructorLayouts { get; init; } = new Dictionary<int, List<ConstructorTypeLayout>>();
    public Mir.ParameterEffectMap ParameterEffects { get; init; } = new();
}

public sealed class MirOutput : ICompilationQueryOutput
{
    public bool IsIncomplete { get; init; }
    public string? IncompleteReason { get; init; }
    public MirModule? MirModule { get; init; }
    public MirModule? BorrowMirModule { get; init; }
}

public sealed class BorrowOutput : ICompilationQueryOutput
{
    public bool IsIncomplete { get; init; }
    public string? IncompleteReason { get; init; }
    public ModuleBorrowCheckResult? BorrowCheckResult { get; init; }
}

public sealed class CodeGenOutput : ICompilationQueryOutput
{
    public bool IsIncomplete { get; init; }
    public string? IncompleteReason { get; init; }
    public LlvmModule? LlvmModule { get; init; }
    public string? LlvmIrText { get; init; }
}
