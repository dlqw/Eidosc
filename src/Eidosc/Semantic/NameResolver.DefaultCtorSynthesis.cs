using Eidosc.Ast.Declarations;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    internal static void RehydrateNamerStructuralRewrites(ModuleDecl root)
    {
        foreach (var declaration in root.Declarations)
        {
            switch (declaration)
            {
                case AdtDef adt:
                    SynthesizeDefaultConstructorIfBareProduct(adt);
                    break;
                case ModuleDecl module:
                    RehydrateNamerStructuralRewrites(module);
                    break;
            }
        }
    }

    /// <summary>
    /// For a product-type ADT without explicit case types, synthesize a default constructor
    /// named after the type. The synthesized constructor shares the
    /// type's fields by reference; downstream phases treat constructors as read-only after
    /// name resolution. This is the single desugaring point for bare product types.
    /// </summary>
    private static void SynthesizeDefaultConstructorIfBareProduct(AdtDef adt)
    {
        if (adt.IsTypeAlias || adt.Constructors.Count > 0 || adt.Cases.Count > 0)
        {
            return;
        }

        var ctor = new Constructor();
        ctor.SetSpan(adt.Span);
        ctor.SetName(adt.Name);
        foreach (var field in adt.Fields)
        {
            ctor.AddNamedArg(field);
        }

        adt.Constructors.Add(ctor);
    }
}
