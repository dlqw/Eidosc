namespace Eidosc.Doc;

/// <summary>
/// 文档生成数据模型
/// </summary>
public sealed class DocModule
{
    public string Name { get; init; } = "";
    public string? Summary { get; init; }
    public List<DocFunction> Functions { get; init; } = [];
    public List<DocType> Types { get; init; } = [];
    public List<DocTrait> Traits { get; init; } = [];
    public List<DocFunction> Constructors { get; init; } = [];
}

public sealed class DocFunction
{
    public string Name { get; init; } = "";
    public string? QualifiedName { get; init; }
    public string? Summary { get; init; }
    public string? Signature { get; init; }
    public string? ReturnType { get; init; }
    public List<DocParam> Parameters { get; init; } = [];
    public List<string> Examples { get; init; } = [];
    public string? Deprecated { get; init; }
    public bool IsExported { get; init; }
}

public sealed class DocType
{
    public string Name { get; init; } = "";
    public string? Summary { get; init; }
    public string? Kind { get; init; }
    public List<DocField> Fields { get; init; } = [];
    public List<DocFunction> Constructors { get; init; } = [];
    public List<string> TypeParams { get; init; } = [];
}

public sealed class DocTrait
{
    public string Name { get; init; } = "";
    public string? Summary { get; init; }
    public List<DocFunction> Methods { get; init; } = [];
}

public sealed class DocField
{
    public string Name { get; init; } = "";
    public string? Summary { get; init; }
    public string? TypeName { get; init; }
}

public sealed class DocParam
{
    public string Name { get; init; } = "";
    public string? TypeName { get; init; }
    public string? Description { get; init; }
}
