using Eidosc.Ast.Declarations;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    /// <summary>
    /// For a bare product-type ADT (fields but no constructor variants), synthesize a
    /// default constructor named after the type so that `T :: type { a: A, b: B }` behaves
    /// exactly like `T :: type { T { a: A, b: B } }`. The synthesized constructor shares the
    /// type's fields by reference; downstream phases treat constructors as read-only after
    /// name resolution. This is the single desugaring point for bare product types.
    /// </summary>
    private static void SynthesizeDefaultConstructorIfBareProduct(AdtDef adt)
    {
        if (adt.Constructors.Count > 0 || adt.Fields.Count == 0)
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
