using Eidosc.Ast.Declarations;

namespace Eidosc.Ast;

internal interface IMetaSyntaxSite
{
    MetaInvocationSyntax Invocation { get; }

    IReadOnlyList<EidosAstNode> MaterializedNodes { get; }

    bool IsMaterialized { get; }

    void SetMaterializedNodes(IReadOnlyList<EidosAstNode> nodes);
}
