using Eidosc.Parsing.Handwritten;
using Eidosc.Parsing.Lexer;
using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands.Migrate;

public static partial class SyntaxMigrationPlanner
{
    private static readonly IReadOnlyDictionary<string, string> Version07MetaMembers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TypeInfo"] = "TypeShape",
            ["Decl"] = "Declaration",
            ["DeclInfo"] = "DeclarationShape",
            ["DeriveInput"] = "Type",
            ["Expansion"] = "Items",
            ["Declaration"] = "Syntax[meta.Item]",
            ["Expr"] = "Syntax[meta.Expr]",
            ["Pattern"] = "Syntax[meta.Pattern]",
            ["Branch"] = "Syntax[meta.Branch]",
            ["FieldInfo"] = "Field",
            ["ConstructorInfo"] = "Constructor",
            ["LayoutInfo"] = "Layout",
            ["typeInfo"] = "shape_of",
            ["typeName"] = "name_of",
            ["decl"] = "declaration_of",
            ["declarationInfo"] = "shape_of",
            ["typeKind"] = "kind_of",
            ["declKind"] = "kind_of",
            ["typeParameters"] = "parameters_of",
            ["constructors"] = "constructors_of",
            ["constructorFields"] = "fields_of",
            ["fieldTypeInfo"] = "type_of",
            ["hasField"] = "has_field",
            ["functionParameters"] = "parameters_of",
            ["functionResult"] = "result_type_of",
            ["functionEffects"] = "effects_of",
            ["referenceMutable"] = "mutability_of",
            ["referenceReferent"] = "referent_of",
            ["traitAssociatedItems"] = "items_of",
            ["traitConstraints"] = "constraints_of",
            ["attributes"] = "clauses_of",
            ["declSpan"] = "span_of",
            ["deriveSpan"] = "span_of",
            ["layoutOf"] = "layout_of",
            ["target"] = "target_type_of",
            ["targetDeclaration"] = "target_declaration_of",
            ["expansion"] = "transformation"
        };

    private static readonly IReadOnlySet<string> Version07StdModules =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Alternative", "Applicative", "Async", "AsyncExtra", "AsyncRuntime", "Barrier",
            "Binary", "BinaryHeap", "Channel", "CommandLine", "Console", "Deque", "FFI",
            "File", "FloatMath", "Fn", "Foldable", "Functor", "GameMath", "Hash", "HashMap",
            "HashSet", "Json", "JsonParser", "JsonValue", "Math", "Monad", "Monoid", "Mutex",
            "Network", "Option", "Ordering", "PersistentMap", "PersistentSet", "Predicate",
            "Prelude", "PriorityQueue", "Promise", "Queue", "Range", "Regex", "Result",
            "RuntimeArray", "RwLock", "Semigroup", "Send", "Seq", "SeqBuilder", "Shared",
            "Stack", "Task", "TaskGroup", "Text", "Time", "Trait", "TraitInvoke", "Traversable",
            "TreeMap", "TreeSet"
        };

    private static readonly IReadOnlyDictionary<string, string> Version07BuildMembers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Context"] = "Session",
            ["context"] = "session",
            ["generatedSource"] = "generated_source",
            ["readText"] = "read_text"
        };

    private static void AddVersion07NamingEdits(
        IReadOnlyList<Token> tokens,
        List<SyntaxMigrationEdit> edits)
    {
        var contentTokens = tokens.OfType<ContentToken>().ToList();
        var importedModuleAliases = CollectVersion07ImportedModuleAliases(contentTokens);
        for (var index = 0; index < contentTokens.Count; index++)
        {
            var token = contentTokens[index];
            var text = GetTokenText(token);
            var previous = index > 0 ? GetTokenText(contentTokens[index - 1]) : string.Empty;
            var next = index + 1 < contentTokens.Count ? GetTokenText(contentTokens[index + 1]) : string.Empty;

            if (TryGetBuiltinRootReplacement(text, previous, next, out var rootReplacement))
            {
                AddNamingEdit(token, rootReplacement, "builtin-namespace", edits);
                continue;
            }

            if (index >= 2 && previous is "." or "::")
            {
                var root = GetTokenText(contentTokens[index - 2]);
                if (root is "Std" or "std" && TryGetVersion07StdModuleName(text, out var stdModuleName))
                {
                    AddNamingEdit(token, stdModuleName, "stdlib-module-name", edits);
                    continue;
                }

                if (root == "Meta")
                {
                    var replacement = Version07MetaMembers.TryGetValue(text, out var mapped)
                        ? mapped
                        : text;
                    AddNamingEdit(token, replacement, "meta-api-name", edits);
                    continue;
                }

                if (root == "Build")
                {
                    var replacement = Version07BuildMembers.TryGetValue(text, out var mapped)
                        ? mapped
                        : text;
                    AddNamingEdit(token, replacement, "build-api-name", edits);
                    continue;
                }
            }

            if (next == "." &&
                previous != "." &&
                importedModuleAliases.TryGetValue(text, out var importedModuleReplacement))
            {
                AddNamingEdit(token, importedModuleReplacement, "imported-module-alias", edits);
                continue;
            }

            if (text is "FFI" or "IO" && IsNeedClauseArgument(contentTokens, index))
            {
                AddNamingEdit(token, NormalizeEffectName(text), "effect-name", edits);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> CollectVersion07ImportedModuleAliases(
        IReadOnlyList<ContentToken> tokens)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (!string.Equals(GetTokenText(tokens[index]), "import", StringComparison.Ordinal))
            {
                continue;
            }

            var moduleIndex = index + 1;
            if (moduleIndex + 2 < tokens.Count &&
                GetTokenText(tokens[moduleIndex]) is "Std" or "std" &&
                GetTokenText(tokens[moduleIndex + 1]) is "." or "::")
            {
                moduleIndex += 2;
            }
            else
            {
                continue;
            }

            var importedName = GetTokenText(tokens[moduleIndex]);
            if (!TryGetVersion07StdModuleName(importedName, out var semanticName))
            {
                continue;
            }

            if (moduleIndex + 2 < tokens.Count &&
                string.Equals(GetTokenText(tokens[moduleIndex + 1]), "as", StringComparison.Ordinal))
            {
                continue;
            }

            aliases[importedName] = semanticName;
        }

        return aliases;
    }

    private static string GetVersion07StdModuleName(string legacyName)
    {
        return legacyName switch
        {
            "Fn" => "Functions",
            "Trait" => "Traits",
            _ => ManifestNamingRules.NormalizeModulePathSegment(legacyName)
        };
    }

    private static bool TryGetVersion07StdModuleName(string spelling, out string semanticName)
    {
        foreach (var candidate in Version07StdModules)
        {
            var candidateSemanticName = GetVersion07StdModuleName(candidate);
            if (string.Equals(candidate, spelling, StringComparison.Ordinal) ||
                string.Equals(candidateSemanticName, spelling, StringComparison.Ordinal) ||
                string.Equals(
                    ManifestNamingRules.NormalizeDependencyAlias(candidateSemanticName),
                    spelling,
                    StringComparison.Ordinal))
            {
                semanticName = candidateSemanticName;
                return true;
            }
        }

        semanticName = string.Empty;
        return false;
    }

    private static bool TryGetBuiltinRootReplacement(
        string text,
        string previous,
        string next,
        out string replacement)
    {
        replacement = text switch
        {
            "Std" => "std",
            "Meta" => "meta",
            "Build" => "build",
            _ => string.Empty
        };
        if (replacement.Length == 0)
        {
            return false;
        }

        return previous is "." or "::" || next is "." or "::" || previous is "import" or "open";
    }

    private static bool IsNeedClauseArgument(IReadOnlyList<ContentToken> tokens, int index)
    {
        for (var cursor = index - 1; cursor >= 0; cursor--)
        {
            var text = GetTokenText(tokens[cursor]);
            if (text == "need")
            {
                return true;
            }

            if (text is ";" or "{" or "}" or "=" or "=>")
            {
                return false;
            }
        }

        return false;
    }

    private static void AddNamingEdit(
        ContentToken token,
        string replacement,
        string kind,
        List<SyntaxMigrationEdit> edits)
    {
        var current = GetTokenText(token);
        if (string.Equals(current, replacement, StringComparison.Ordinal))
        {
            return;
        }

        edits.Add(new SyntaxMigrationEdit(
            token.Location.Position,
            token.Length,
            replacement,
            kind,
            $"Rename '{current}' to its Eidos 0.7 semantic spelling '{replacement}'."));
    }

    private static string ToLowerSnakeCase(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current) && index > 0)
            {
                var previous = value[index - 1];
                var hasFollowingLower = index + 1 < value.Length && char.IsLower(value[index + 1]);
                if (char.IsLower(previous) || char.IsDigit(previous) || hasFollowingLower)
                {
                    builder.Append('_');
                }
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}
