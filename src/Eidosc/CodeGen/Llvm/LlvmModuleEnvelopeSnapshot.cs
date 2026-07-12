using Eidosc.Pipeline;

namespace Eidosc.CodeGen.Llvm;

public sealed record LlvmModuleEnvelopeSnapshot(
    string SchemaVersion,
    string ModuleName,
    string SourceFilename,
    string DataLayout,
    string TargetTriple,
    IReadOnlyList<string> HeaderIr,
    IReadOnlyList<string> TypeDefinitionIr,
    IReadOnlyList<LlvmTypeDefinitionEnvelopeFragment> TypeDefinitionFragments,
    IReadOnlyList<string> GlobalIr,
    IReadOnlyList<LlvmGlobalEnvelopeFragment> GlobalFragments,
    IReadOnlyList<string> DeclarationIr,
    IReadOnlyList<LlvmDeclarationEnvelopeFragment> DeclarationFragments,
    IReadOnlyList<string> AttributeGroupIr,
    IReadOnlyList<string> LinkLibraries,
    IReadOnlyList<string> LinkLibraryPaths,
    IReadOnlyList<string> NativeSources,
    IReadOnlyList<string> NativeIncludePaths,
    IReadOnlyList<string> LinkerFlags)
{
    public const string CurrentSchemaVersion = "llvm-module-envelope-snapshot-v1";

    public static LlvmModuleEnvelopeSnapshot FromModule(
        LlvmModule module,
        string dataLayout,
        string targetTriple)
    {
        var typeDefinitions = CollectTypeDefinitions(module).ToArray();
        var typeDefinitionFragments = typeDefinitions.Select(CreateTypeDefinitionFragment).ToArray();
        var globalFragments = module.Globals.Select(CreateGlobalFragment).ToArray();
        var declarationFragments = module.Declarations.Select(CreateDeclarationFragment).ToArray();

        return new LlvmModuleEnvelopeSnapshot(
            CurrentSchemaVersion,
            module.Name,
            module.SourceFilename,
            dataLayout,
            targetTriple,
            [
                $"; ModuleID = '{module.Name}'",
                $"source_filename = \"{module.Name}\"",
                "",
                $"target datalayout = \"{dataLayout}\"",
                $"target triple = \"{targetTriple}\""
            ],
            typeDefinitionFragments.Select(static fragment => fragment.DefinitionIr).ToArray(),
            typeDefinitionFragments,
            globalFragments.Select(static fragment => fragment.DefinitionIr).ToArray(),
            globalFragments,
            declarationFragments.Select(static fragment => fragment.DeclarationIr).ToArray(),
            declarationFragments,
            module.AttributeGroups.Select(FormatAttributeGroup).ToArray(),
            module.LinkLibraries.ToArray(),
            module.LinkLibraryPaths.ToArray(),
            module.NativeSources.ToArray(),
            module.NativeIncludePaths.ToArray(),
            module.LinkerFlags.ToArray());
    }

    public string EnvelopeFingerprint => ModuleArtifactHash.ComputeJsonHash(new
    {
        SchemaVersion,
        ModuleName,
        SourceFilename,
        DataLayout,
        TargetTriple,
        HeaderIr,
        TypeDefinitionIr,
        TypeDefinitionFragments,
        GlobalIr,
        GlobalFragments,
        DeclarationIr,
        DeclarationFragments,
        AttributeGroupIr,
        LinkLibraries,
        LinkLibraryPaths,
        NativeSources,
        NativeIncludePaths,
        LinkerFlags
    });

    public int FragmentLineCount =>
        HeaderIr.Count +
        TypeDefinitionIr.Count +
        GlobalIr.Count +
        DeclarationIr.Count +
        AttributeGroupIr.Count;

    public IReadOnlyList<string> ObjectGroupGlobalIr =>
        GlobalFragments.Count == 0
            ? GlobalIr
            : GlobalFragments.Select(static fragment => fragment.IsLocalToObject
                ? fragment.DefinitionIr
                : fragment.DeclarationIr).ToArray();

    public IReadOnlyList<string> GetObjectGroupGlobalIr(IReadOnlySet<string> referencedSymbols)
    {
        if (GlobalFragments.Count == 0)
        {
            return GlobalIr;
        }

        return GlobalFragments
            .Where(fragment => referencedSymbols.Contains(fragment.Name))
            .Select(static fragment => fragment.IsLocalToObject
                ? fragment.DefinitionIr
                : fragment.DeclarationIr)
            .ToArray();
    }

    public IReadOnlyList<string> GetObjectGroupTypeDefinitionIr(IReadOnlySet<string> referencedTypeNames)
    {
        if (TypeDefinitionFragments.Count == 0)
        {
            return TypeDefinitionIr;
        }

        var fragmentByName = TypeDefinitionFragments.ToDictionary(static fragment => fragment.Name, StringComparer.Ordinal);
        var closure = new SortedSet<string>(referencedTypeNames, StringComparer.Ordinal);
        var queue = new Queue<string>(closure);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!fragmentByName.TryGetValue(name, out var fragment))
            {
                continue;
            }

            foreach (var dependency in fragment.ReferencedTypeNames)
            {
                if (closure.Add(dependency))
                {
                    queue.Enqueue(dependency);
                }
            }
        }

        return TypeDefinitionFragments
            .Where(fragment => closure.Contains(fragment.Name))
            .Select(static fragment => fragment.DefinitionIr)
            .ToArray();
    }

    public IReadOnlyList<string> GetObjectGroupDeclarationIr(IReadOnlySet<string> referencedSymbols)
    {
        if (DeclarationFragments.Count == 0)
        {
            return DeclarationIr;
        }

        return DeclarationFragments
            .Where(fragment => referencedSymbols.Contains(fragment.Name))
            .Select(static fragment => fragment.DeclarationIr)
            .ToArray();
    }

    private static IReadOnlyList<LlvmStructType> CollectTypeDefinitions(LlvmModule module)
    {
        var definitions = new List<LlvmStructType>();
        foreach (var namedStruct in module.NamedStructTypes)
        {
            CollectTypesFromType(namedStruct, definitions);
        }

        foreach (var global in module.Globals)
        {
            CollectTypesFromType(global.Type, definitions);
        }

        foreach (var function in module.Functions)
        {
            CollectTypesFromType(function.ReturnType, definitions);
            foreach (var parameter in function.Parameters)
            {
                CollectTypesFromType(parameter.Type, definitions);
            }

            foreach (var block in function.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is LlvmGetElementPtr { StructType: { } gepStructType })
                    {
                        CollectTypesFromType(gepStructType, definitions);
                    }
                }
            }
        }

        return definitions;
    }

    private static void CollectTypesFromType(LlvmType type, List<LlvmStructType> definitions)
    {
        if (type is not LlvmStructType structType ||
            string.IsNullOrEmpty(structType.Name) ||
            definitions.Contains(structType))
        {
            return;
        }

        definitions.Add(structType);
        foreach (var field in structType.Fields)
        {
            CollectTypesFromType(field, definitions);
        }
    }

    private static string FormatTypeDefinition(LlvmStructType type)
    {
        var fields = string.Join(", ", type.Fields.Select(static field => field.ToIrString()));
        return $"%struct.{type.Name} = type {{ {fields} }}";
    }

    private static LlvmTypeDefinitionEnvelopeFragment CreateTypeDefinitionFragment(LlvmStructType type)
    {
        var referencedTypes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var field in type.Fields)
        {
            CollectNamedTypes(field, referencedTypes);
        }

        return new LlvmTypeDefinitionEnvelopeFragment(
            type.Name ?? "",
            FormatTypeDefinition(type),
            referencedTypes.ToArray());
    }

    private static void CollectNamedTypes(LlvmType type, ISet<string> names)
    {
        switch (type)
        {
            case LlvmStructType { IsLiteral: false, Name: { Length: > 0 } name } structType:
                names.Add(name);
                foreach (var field in structType.Fields)
                {
                    CollectNamedTypes(field, names);
                }

                break;
            case LlvmStructType structType:
                foreach (var field in structType.Fields)
                {
                    CollectNamedTypes(field, names);
                }

                break;
            case LlvmArrayType arrayType:
                CollectNamedTypes(arrayType.Element, names);
                break;
            case LlvmVectorType vectorType:
                CollectNamedTypes(vectorType.ElementType, names);
                break;
            case LlvmPointerType { ElementType: { } elementType }:
                CollectNamedTypes(elementType, names);
                break;
            case LlvmFunctionType functionType:
                CollectNamedTypes(functionType.ReturnType, names);
                foreach (var parameterType in functionType.ParameterTypes)
                {
                    CollectNamedTypes(parameterType, names);
                }

                break;
        }
    }

    private static string FormatGlobal(LlvmGlobal global)
    {
        var linkage = global.Linkage == LlvmLinkage.External ? "" : $"{global.Linkage.ToIrString()} ";
        var storageClass = global.IsConstant ? "constant" : "global";
        var initializer = global.Initializer != null
            ? $" {global.Initializer.ToIrString()}"
            : " zeroinitializer";
        return $"@{global.Name} = {linkage}{storageClass} {global.Type.ToIrString()}{initializer}";
    }

    private static LlvmGlobalEnvelopeFragment CreateGlobalFragment(LlvmGlobal global)
    {
        var definition = FormatGlobal(global);
        var storageClass = global.IsConstant ? "constant" : "global";
        var declaration = $"@{global.Name} = external {storageClass} {global.Type.ToIrString()}";
        return new LlvmGlobalEnvelopeFragment(
            global.Name,
            global.Linkage.ToString(),
            global.Type.ToIrString(),
            global.IsConstant,
            IsLocalToObject(global.Linkage),
            definition,
            declaration);
    }

    private static bool IsLocalToObject(LlvmLinkage linkage) =>
        linkage is LlvmLinkage.Private or LlvmLinkage.Internal;

    private static string FormatDeclaration(LlvmDeclaration declaration)
    {
        var name = $"@{declaration.Name}";
        if (declaration.Type is LlvmFunctionType functionType)
        {
            var parameters = string.Join(", ", functionType.ParameterTypes.Select(static parameter => parameter.ToIrString()));
            if (functionType.IsVarArg)
            {
                parameters = string.IsNullOrEmpty(parameters) ? "..." : $"{parameters}, ...";
            }

            return $"declare {functionType.ReturnType.ToIrString()} {name}({parameters})";
        }

        return $"declare {declaration.Type.ToIrString()} {name}";
    }

    private static LlvmDeclarationEnvelopeFragment CreateDeclarationFragment(LlvmDeclaration declaration) =>
        new(
            declaration.Name,
            declaration.Origin.ToString(),
            declaration.Type.ToIrString(),
            FormatDeclaration(declaration));

    private static string FormatAttributeGroup(LlvmAttributeGroup attributeGroup) =>
        $"attributes #{attributeGroup.Id} = {{ {attributeGroup.Attributes} }}";
}

public sealed record LlvmGlobalEnvelopeFragment(
    string Name,
    string Linkage,
    string TypeIr,
    bool IsConstant,
    bool IsLocalToObject,
    string DefinitionIr,
    string DeclarationIr);

public sealed record LlvmTypeDefinitionEnvelopeFragment(
    string Name,
    string DefinitionIr,
    IReadOnlyList<string> ReferencedTypeNames);

public sealed record LlvmDeclarationEnvelopeFragment(
    string Name,
    string Origin,
    string TypeIr,
    string DeclarationIr);
