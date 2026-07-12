using Eidosc.Ast.Declarations;
using Eidosc.ProjectSystem;

namespace Eidosc.Parsing.Handwritten;

public static class SyntaxParser
{
    public static (ModuleDecl? Ast, List<Diagnostic.Diagnostic> Diagnostics)
        Parse(
            IReadOnlyList<Token> tokens,
            string sourcePath,
            string languageVersion = EidosLanguageVersions.Current)
    {
        var ctx = new ParserContext(tokens, sourcePath, languageVersion);
        var declParser = new DeclParser(ctx);
        var nodes = declParser.ParseProgram();

        var module = new ModuleDecl();
        module.SetSpan(nodes.Count > 0 ? nodes[0].Span : Utils.SourceSpan.Empty);
        module.SetPath([System.IO.Path.GetFileNameWithoutExtension(sourcePath)]);

        var declarations = new List<Declaration>();
        foreach (var node in nodes)
        {
            if (node is Declaration d)
                declarations.Add(d);
        }
        module.SetDeclarations(declarations);

        return (module, ctx.Diagnostics.ToList());
    }
}
