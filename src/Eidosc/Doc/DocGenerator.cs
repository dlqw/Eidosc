using Eidosc.Symbols;
using Eidosc.Pipeline;
using Eidosc.Semantic;

namespace Eidosc.Doc;

/// <summary>
/// 从 CompilationResult 生成 DocModel。
/// </summary>
public static class DocGenerator
{
    public static DocModule Generate(CompilationResult result)
    {
        var moduleName = result.Ast?.Path.Count > 0
            ? string.Join("/", result.Ast.Path)
            : Path.GetFileNameWithoutExtension(result.InputFile);
        var docs = result.Documentation;

        var docModule = new DocModule { Name = moduleName };

        if (result.SymbolTable == null)
            return docModule;

        var symbolTable = result.SymbolTable;

        foreach (var (symbolId, symbol) in symbolTable.Symbols)
        {
            if (!symbol.IsModuleLevel || !symbol.IsPublic)
                continue;

            var docComment = GetDocComment(symbol, docs);

            switch (symbol)
            {
                case FuncSymbol func:
                    docModule.Functions.Add(BuildDocFunction(func, symbolTable, docComment));
                    break;
                case AdtSymbol adt:
                    docModule.Types.Add(BuildDocType(adt, symbolTable, docComment));
                    break;
                case TraitSymbol trait:
                    docModule.Traits.Add(BuildDocTrait(trait, symbolTable, docComment));
                    break;
            }
        }

        return docModule;
    }

    private static DocComment? GetDocComment(Symbol symbol, IReadOnlyDictionary<int, DocComment>? docs)
    {
        if (docs == null)
            return null;

        var line = symbol.Span.Location.Line;
        return docs.TryGetValue(line, out var doc) ? doc : null;
    }

    private static DocFunction BuildDocFunction(
        FuncSymbol func,
        SymbolTable symbolTable,
        DocComment? doc)
    {
        var parameters = new List<DocParam>();
        for (var i = 0; i < func.Parameters.Count; i++)
        {
            var paramId = func.Parameters[i];
            var paramSymbol = symbolTable.GetSymbol(paramId) as VarSymbol;
            var paramType = i < func.ParamTypes.Count ? func.ParamTypes[i].ToString() : null;

            var paramDoc = doc?.Params.FirstOrDefault(p =>
                string.Equals(p.Name, paramSymbol?.Name, StringComparison.Ordinal));

            parameters.Add(new DocParam
            {
                Name = paramSymbol?.Name ?? $"arg{i}",
                TypeName = paramType,
                Description = paramDoc?.Description
            });
        }

        var returnType = func.ReturnType.IsValid ? func.ReturnType.ToString() : null;

        return new DocFunction
        {
            Name = func.Name,
            QualifiedName = func.Name,
            Summary = doc?.Summary,
            Signature = BuildSignature(func.Name, parameters, returnType),
            ReturnType = returnType,
            Parameters = parameters,
            Examples = doc?.Examples ?? [],
            Deprecated = doc?.Deprecated,
            IsExported = func.IsPublic
        };
    }

    private static DocType BuildDocType(
        AdtSymbol adt,
        SymbolTable symbolTable,
        DocComment? doc)
    {
        var constructors = new List<DocFunction>();
        foreach (var ctorId in adt.Constructors)
        {
            var ctorSymbol = symbolTable.GetSymbol(ctorId) as CtorSymbol;
            if (ctorSymbol != null)
            {
                constructors.Add(new DocFunction
                {
                    Name = ctorSymbol.Name,
                    Summary = null,
                    Parameters = ctorSymbol.PositionalArgs.Select((t, i) => new DocParam
                    {
                        Name = $"field{i}",
                        TypeName = t.IsValid ? t.ToString() : null
                    }).ToList()
                });
            }
        }

        var fields = new List<DocField>();
        foreach (var fieldId in adt.Fields)
        {
            var fieldSymbol = symbolTable.GetSymbol(fieldId) as FieldSymbol;
            if (fieldSymbol != null)
            {
                fields.Add(new DocField
                {
                    Name = fieldSymbol.Name,
                    TypeName = fieldSymbol.FieldType.IsValid ? fieldSymbol.FieldType.ToString() : null
                });
            }
        }

        return new DocType
        {
            Name = adt.Name,
            Summary = doc?.Summary,
            Kind = adt.IsTypeAlias ? "type alias" : adt.IsCStruct ? "cstruct" : "type",
            Fields = fields,
            Constructors = constructors,
            TypeParams = []
        };
    }

    private static DocTrait BuildDocTrait(
        TraitSymbol trait,
        SymbolTable symbolTable,
        DocComment? doc)
    {
        var methods = new List<DocFunction>();
        foreach (var methodId in trait.Methods)
        {
            var methodSymbol = symbolTable.GetSymbol(methodId) as FuncSymbol;
            if (methodSymbol != null)
            {
                methods.Add(new DocFunction
                {
                    Name = methodSymbol.Name,
                    Summary = null,
                    Parameters = methodSymbol.Parameters.Select((p, i) => new DocParam
                    {
                        Name = symbolTable.GetSymbol(p)?.Name ?? $"arg{i}",
                        TypeName = i < methodSymbol.ParamTypes.Count ? methodSymbol.ParamTypes[i].ToString() : null
                    }).ToList(),
                    ReturnType = methodSymbol.ReturnType.IsValid ? methodSymbol.ReturnType.ToString() : null
                });
            }
        }

        return new DocTrait
        {
            Name = trait.Name,
            Summary = doc?.Summary,
            Methods = methods
        };
    }

    private static string BuildSignature(string name, List<DocParam> parameters, string? returnType)
    {
        var parameterTypes = parameters
            .Select(static parameter => string.IsNullOrWhiteSpace(parameter.TypeName) ? "_" : parameter.TypeName)
            .ToArray();
        var argumentText = parameterTypes.Length switch
        {
            0 => "Unit",
            1 => parameterTypes[0],
            _ => $"({string.Join(", ", parameterTypes)})"
        };
        return $"{name} :: {argumentText} -> {returnType ?? "_"}";
    }
}
