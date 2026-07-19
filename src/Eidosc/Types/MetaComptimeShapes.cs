using Eidosc.Ast.Declarations;
using Eidosc.Symbols;

namespace Eidosc.Types;

internal static partial class MetaComptimeIntrinsics
{
    private static ComptimeAdtValue BuildTypeShape(
        ComptimeTypeValue typeValue,
        MetaComptimeContext meta)
    {
        var type = typeValue.TypeRef;
        var symbol = type.SymbolId.IsValid ? meta.SymbolTable.GetSymbol(type.SymbolId) : null;
        var caseName = ClassifyTypeShapeCase(type, symbol);
        var payloadType = caseName switch
        {
            "Function" or "ForeignFunction" => (WellKnownStrings.Meta.Types.FunctionShape, WellKnownTypeIds.MetaFunctionShapeId, "function-shape"),
            "Reference" or "RawPointer" => (WellKnownStrings.Meta.Types.ReferenceShape, WellKnownTypeIds.MetaReferenceShapeId, "reference-shape"),
            "Tuple" => (WellKnownStrings.Meta.Types.TupleShape, WellKnownTypeIds.MetaTupleShapeId, "tuple-shape"),
            "ClosedSum" => (WellKnownStrings.Meta.Types.ClosedSumShape, WellKnownTypeIds.MetaClosedSumShapeId, "closed-sum-shape"),
            "Case" => (WellKnownStrings.Meta.Types.CaseShape, WellKnownTypeIds.MetaCaseShapeId, "case-shape"),
            _ => (WellKnownStrings.Meta.Types.NominalShape, WellKnownTypeIds.MetaNominalShapeId, "nominal-shape")
        };

        var parameters = List(type.Arguments.Select(static argument =>
            (ComptimeValue)new ComptimeTypeValue(argument)));
        var genericArguments = List((type.GenericArguments ?? []).Select(argument =>
            (ComptimeValue)CreateGenericArgumentShape(argument)));
        var declaration = symbol == null
            ? (ComptimeValue)ComptimeUnitValue.Instance
            : CreateDeclValue(symbol, meta.SymbolTable);
        var properties = new List<(string Name, ComptimeValue Value)>
        {
            ("schemaVersion", new ComptimeIntegerValue(WellKnownStrings.Meta.SchemaVersion)),
            ("kind", new ComptimeStringValue(ToCanonicalShapeKind(caseName))),
            ("type", typeValue),
            ("name", new ComptimeStringValue(type.Name)),
            ("declaration", declaration),
            ("parameters", parameters),
            ("genericArguments", genericArguments),
            ("constructors", List([])),
            ("fields", List([])),
            ("declaredFields", List([])),
            ("commonFields", List([])),
            ("localFields", List([])),
            ("directCases", List([])),
            ("leafCases", List([])),
            ("parentType", ComptimeUnitValue.Instance),
            ("parentSpecialization", new ComptimeStringValue("")),
            ("functionParameters", List([])),
            ("functionResult", ComptimeUnitValue.Instance),
            ("functionEffects", List([])),
            ("callingConvention", new ComptimeStringValue(caseName == "ForeignFunction" ? "c" : "eidos")),
            ("referenceMutable", new ComptimeBoolValue(type.Kind == MetaTypeKind.MutableReference)),
            ("referenceKind", new ComptimeStringValue(GetReferenceKind(type.Kind))),
            ("referenceReferent", type.Arguments.Count > 0
                ? new ComptimeTypeValue(type.Arguments[0])
                : ComptimeUnitValue.Instance),
            ("borrowConstraints", List([])),
            ("higherKind", new ComptimeStringValue(type.Kind == MetaTypeKind.HigherKinded ? type.Name : "kind1")),
            ("associatedItems", List([])),
            ("constraints", List([])),
            ("clauses", List([])),
            ("canonicalTarget", ComptimeUnitValue.Instance),
            ("generatedOrigin", symbol?.GeneratedOrigin == null
                ? ComptimeUnitValue.Instance
                : CreateOriginShape(symbol.GeneratedOrigin, meta)),
            ("sealed", new ComptimeBoolValue(symbol is AdtSymbol { IsClosedSum: true })),
            ("leaf", new ComptimeBoolValue(symbol is AdtSymbol { IsCaseType: true, DirectCases.Count: 0 })),
            ("subtypeInjection", new ComptimeBoolValue(symbol is AdtSymbol { IsCaseType: true }))
        };

        PopulateStructuralTypeShape(typeValue, type, symbol, meta, properties);
        var payload = TypedShapeObject(
            payloadType.Item3,
            payloadType.Item1,
            payloadType.Item2,
            properties);
        return CreateShapeCase(
            WellKnownTypeIds.MetaTypeShapeId,
            WellKnownStrings.Meta.Types.TypeShape,
            caseName,
            payload,
            meta.SymbolTable);
    }

    private static void PopulateStructuralTypeShape(
        ComptimeTypeValue typeValue,
        MetaTypeRef type,
        Symbol? symbol,
        MetaComptimeContext meta,
        List<(string Name, ComptimeValue Value)> properties)
    {
        if (type.Kind is MetaTypeKind.Function or MetaTypeKind.ForeignFunction && type.Arguments.Count > 0)
        {
            properties.Replace(
                "functionParameters",
                List(type.Arguments.Take(type.Arguments.Count - 1).Select(static argument =>
                    (ComptimeValue)new ComptimeTypeValue(argument))));
            properties.Replace("functionResult", new ComptimeTypeValue(type.Arguments[^1]));
            properties.Replace(
                "functionEffects",
                List((type.GenericArguments ?? [])
                    .Where(static argument => argument.Domain == MetaGenericArgumentDomain.EffectRow)
                    .SelectMany(static argument => argument.Type?.Arguments ?? [])
                    .Select(static effect => (ComptimeValue)new ComptimeTypeValue(effect))));
            properties.Replace(
                "borrowConstraints",
                List(type.Arguments
                    .Select((argument, index) => CreateOwnershipProjection(
                        argument,
                        index == type.Arguments.Count - 1 ? "result" : "parameter",
                        index == type.Arguments.Count - 1 ? -1 : index))));
        }

        if (symbol is AdtSymbol adtSymbol &&
            TryGetAdtAndCase(type.SymbolId, meta, out var root, out var casePath))
        {
            PopulateAdtShape(typeValue, adtSymbol, root, casePath, meta, properties);
        }
        else if (symbol is TraitSymbol &&
                 meta.TraitDefinitions.TryGetValue(type.SymbolId, out var trait))
        {
            var items = trait.Methods
                .Select(method => meta.SymbolTable.GetSymbol(method.SymbolId))
                .Where(static item => item != null)
                .Select(item => (ComptimeValue)BuildDeclarationShape(
                    CreateDeclValue(item!, meta.SymbolTable),
                    meta))
                .Concat(trait.AssociatedTypes.Select(associated =>
                    (ComptimeValue)CreateAssociatedDeclarationShape(
                        "AssociatedType",
                        associated.Name,
                        associated.Span,
                        associated.TypeParams,
                        symbol,
                        meta)))
                .Concat(trait.AssociatedConsts.Select(associated =>
                    (ComptimeValue)CreateAssociatedDeclarationShape(
                        "AssociatedConst",
                        associated.Name,
                        associated.Span,
                        [],
                        symbol,
                        meta)))
                .ToArray();
            properties.Replace("associatedItems", List(items));
            properties.Replace(
                "constraints",
                List(trait.SuperTraits.Select(static constraint =>
                    (ComptimeValue)new ComptimeStringValue(constraint.ToString()))));
            properties.Replace("clauses", CreateClauseList(trait.BoundClauses, meta));
        }
    }

    private static ComptimeValue CreateOwnershipProjection(
        MetaTypeRef type,
        string role,
        int ordinal) =>
        Obj(
            "ownership-slot",
            ("role", new ComptimeStringValue(role)),
            ("ordinal", new ComptimeIntegerValue(ordinal)),
            ("kind", new ComptimeStringValue(type.Kind switch
            {
                MetaTypeKind.Reference => "sharedBorrow",
                MetaTypeKind.MutableReference => "mutableBorrow",
                _ => "byValue"
            })),
            ("type", new ComptimeTypeValue(type)),
            ("deferred", new ComptimeBoolValue(type.Kind == MetaTypeKind.TypeParameter)));

    private static void PopulateAdtShape(
        ComptimeTypeValue typeValue,
        AdtSymbol symbol,
        AdtDef root,
        IReadOnlyList<CaseTypeDef> casePath,
        MetaComptimeContext meta,
        List<(string Name, ComptimeValue Value)> properties)
    {
        properties.Replace(
            "constructors",
            List(root.Constructors.Select(constructor =>
                (ComptimeValue)CreateConstructorInfo(constructor, root, meta))));

        var declaredFields = casePath.Count == 0 ? root.Fields : casePath[^1].Fields;
        var effectiveFields = root.Fields.Concat(casePath.SelectMany(static item => item.Fields)).ToArray();
        properties.Replace(
            "declaredFields",
            List(declaredFields.Select(field =>
                (ComptimeValue)CreateFieldInfo(
                    field.Name,
                    field.Type,
                    field.SymbolId,
                    field.Span,
                    root,
                    meta,
                    typeValue))));
        properties.Replace(
            "localFields",
            List(declaredFields.Select(field =>
                (ComptimeValue)CreateFieldInfo(
                    field.Name,
                    field.Type,
                    field.SymbolId,
                    field.Span,
                    root,
                    meta,
                    typeValue))));
        properties.Replace(
            "commonFields",
            List(root.Fields.Select(field =>
                (ComptimeValue)CreateFieldInfo(
                    field.Name,
                    field.Type,
                    field.SymbolId,
                    field.Span,
                    root,
                    meta,
                    typeValue))));
        properties.Replace(
            "fields",
            List(effectiveFields.Select(field =>
                (ComptimeValue)CreateFieldInfo(
                    field.Name,
                    field.Type,
                    field.SymbolId,
                    field.Span,
                    root,
                    meta,
                    typeValue))));

        var directCases = symbol.DirectCases
            .Select(meta.SymbolTable.GetSymbol)
            .Where(static item => item != null)
            .Select(item => (ComptimeValue)CreateTypeValue(item!, meta.SymbolTable));
        var leafCases = meta.SymbolTable.GetClosedCaseLeafCases(symbol.Id)
            .Select(meta.SymbolTable.GetSymbol)
            .Where(static item => item != null)
            .Select(item => (ComptimeValue)CreateTypeValue(item!, meta.SymbolTable));
        properties.Replace("directCases", List(directCases));
        properties.Replace("leafCases", List(leafCases));

        if (symbol.ParentAdt.IsValid &&
            meta.SymbolTable.GetSymbol(symbol.ParentAdt) is { } parent &&
            TryProjectTypeValue(typeValue, parent, meta.SymbolTable, out var projected))
        {
            properties.Replace("parentType", projected);
            properties.Replace(
                "parentSpecialization",
                new ComptimeStringValue(symbol.CanonicalParentSpecialization));
        }

        if (root.IsTypeAlias && root.AliasTarget != null)
        {
            var canonicalTarget = CreateTypeRef(root.AliasTarget, meta.SymbolTable, root);
            if (!canonicalTarget.SymbolId.IsValid ||
                CanAccessDeclaration(canonicalTarget.SymbolId, requireBody: false, meta, out _))
            {
                properties.Replace("canonicalTarget", new ComptimeTypeValue(canonicalTarget));
            }
        }

        var clauses = casePath.Count == 0
            ? root.BoundClauses
            : casePath[^1].BoundClauses;
        properties.Replace("clauses", CreateClauseList(clauses, meta));
    }

    private static ComptimeAdtValue BuildDeclarationShape(
        ComptimeDeclValue declaration,
        MetaComptimeContext meta)
    {
        var symbol = declaration.SymbolId.IsValid
            ? meta.SymbolTable.GetSymbol(declaration.SymbolId)
            : null;
        var declarationNode = declaration.SymbolId.IsValid
            ? meta.DeclarationDefinitions.GetValueOrDefault(declaration.SymbolId)
            : null;
        var caseName = ClassifyDeclarationShapeCase(symbol, declarationNode, declaration.DeclarationKind);
        var clauses = declarationNode != null
            ? CreateClauseList(declarationNode.BoundClauses, meta)
            : List([]);
        var genericArguments = List(GetGenericParameterIds(symbol)
            .Select(meta.SymbolTable.GetSymbol<TypeParamSymbol>)
            .Where(static parameter => parameter != null)
            .Select(parameter => (ComptimeValue)CreateGenericArgumentShape(
                CreateGenericParameterRef(parameter!, meta.SymbolTable))));
        return CreateDeclarationShapeCase(
            caseName,
            declaration,
            symbol?.IsPublic == true,
            genericArguments,
            clauses,
            symbol?.GeneratedOrigin,
            meta);
    }

    private static ComptimeAdtValue CreateDeclarationShapeCase(
        string caseName,
        ComptimeDeclValue declaration,
        bool isPublic,
        ComptimeValue genericArguments,
        ComptimeValue clauses,
        GeneratedDeclarationOrigin? generatedOrigin,
        MetaComptimeContext meta)
    {
        var properties = new List<(string Name, ComptimeValue Value)>
        {
            ("schemaVersion", new ComptimeIntegerValue(WellKnownStrings.Meta.SchemaVersion)),
            ("decl", declaration),
            ("name", new ComptimeStringValue(declaration.Name)),
            ("kind", new ComptimeStringValue(ToCanonicalShapeKind(caseName))),
            ("public", new ComptimeBoolValue(isPublic)),
            ("genericArguments", genericArguments),
            ("clauses", clauses),
            ("span", CreateSpan(declaration.Span, meta.SymbolTable)),
            ("generatedOrigin", generatedOrigin == null
                ? ComptimeUnitValue.Instance
                : CreateOriginShape(generatedOrigin, meta))
        };
        var payload = TypedShapeObject(
            "declaration-shape",
            WellKnownStrings.Meta.Types.DeclarationShape,
            WellKnownTypeIds.MetaDeclarationShapeId,
            properties);
        return CreateShapeCase(
            WellKnownTypeIds.MetaDeclarationShapeId,
            WellKnownStrings.Meta.Types.DeclarationShape,
            caseName,
            payload,
            meta.SymbolTable);
    }

    private static string ClassifyTypeShapeCase(MetaTypeRef type, Symbol? symbol)
    {
        var structuralCase = type.Kind switch
        {
            MetaTypeKind.Tuple => "Tuple",
            MetaTypeKind.Function => "Function",
            MetaTypeKind.ForeignFunction => "ForeignFunction",
            MetaTypeKind.Reference or MetaTypeKind.MutableReference or MetaTypeKind.SharedReference => "Reference",
            MetaTypeKind.RawPointer => "RawPointer",
            MetaTypeKind.TypeParameter => "TypeParameter",
            MetaTypeKind.HigherKinded => "HigherKinded",
            MetaTypeKind.AssociatedProjection => "AssociatedProjection",
            MetaTypeKind.EffectRow => "EffectRow",
            MetaTypeKind.EffectRequest => "EffectRequest",
            MetaTypeKind.Proof => "Opaque",
            MetaTypeKind.Error => "Error",
            _ => null
        };
        if (structuralCase != null) return structuralCase;

        if (symbol is AdtSymbol adt)
        {
            if (adt.IsTypeAlias) return "Alias";
            if (BaseTypes.IsBuiltIn(adt.TypeId)) return "Primitive";
            if (adt.IsCaseType) return "Case";
            if (adt.IsClosedSum) return "ClosedSum";
            if (adt.IsCStruct) return "Foreign";
            return "Nominal";
        }

        if (symbol is TraitSymbol) return "Trait";
        if (symbol is EffectSymbol) return "Effect";
        return type.Kind switch
        {
            MetaTypeKind.Primitive => "Primitive",
            MetaTypeKind.Nominal => "Nominal",
            MetaTypeKind.ForeignNominal => "Foreign",
            MetaTypeKind.ClosedSum => "ClosedSum",
            MetaTypeKind.Case => "Case",
            MetaTypeKind.Alias => "Alias",
            MetaTypeKind.Trait => "Trait",
            MetaTypeKind.Effect => "Effect",
            _ => throw new InvalidOperationException(
                $"Meta schema {WellKnownStrings.Meta.SchemaVersion} has no TypeShape case for semantic kind '{type.Kind}'.")
        };
    }

    private static string ClassifyDeclarationShapeCase(
        Symbol? symbol,
        Declaration? declaration,
        string fallbackKind)
    {
        if (symbol?.GeneratedOrigin != null) return "Generated";
        if (symbol?.Kind == SymbolKind.Proof) return "Proof";
        if (declaration is FuncDef function &&
            function.BoundClauses.Any(static clause => clause.Kind == DeclarationClauseKind.Operator))
        {
            return "Operator";
        }

        return symbol switch
        {
            ModuleSymbol => "Module",
            AdtSymbol { IsCaseType: true } => "CaseType",
            AdtSymbol => "Type",
            TraitSymbol => "Trait",
            EffectSymbol => "Effect",
            CtorSymbol => "Constructor",
            FieldSymbol => "Field",
            FuncSymbol => "Function",
            TypeParamSymbol => "GenericParameter",
            ImplSymbol => "Instance",
            VarSymbol { IsParameter: true } => "Parameter",
            VarSymbol => "Variable",
            null => fallbackKind switch
            {
                "module" => "Module",
                "adt" or "typealias" => "Type",
                "trait" => "Trait",
                "effect" => "Effect",
                "constructor" => "Constructor",
                "field" => "Field",
                "function" => "Function",
                "typeparameter" => "GenericParameter",
                "impl" => "Instance",
                "variable" => "Variable",
                "proof" => "Proof",
                _ => "Variable"
            },
            _ => throw new InvalidOperationException(
                $"Meta schema {WellKnownStrings.Meta.SchemaVersion} has no DeclarationShape case for symbol kind '{symbol.Kind}'.")
        };
    }

    private static IReadOnlyList<SymbolId> GetGenericParameterIds(Symbol? symbol) => symbol switch
    {
        AdtSymbol adt => adt.TypeParams,
        TraitSymbol trait => trait.TypeParams,
        FuncSymbol function => function.TypeParams,
        CtorSymbol constructor => constructor.TypeParams,
        _ => []
    };

    private static ComptimeMetaObjectValue CreateGenericArgumentShape(MetaGenericArgumentRef argument) =>
        TypedShapeObject(
            "generic-argument",
            WellKnownStrings.Meta.Types.GenericArgument,
            WellKnownTypeIds.MetaGenericArgumentId,
            [
                ("domain", new ComptimeStringValue(argument.Domain.ToToken())),
                ("display", new ComptimeStringValue(argument.Display)),
                ("identity", new ComptimeStringValue(argument.StableIdentity)),
                ("type", argument.Type == null
                    ? ComptimeUnitValue.Instance
                    : new ComptimeTypeValue(argument.Type))
            ]);

    private static ComptimeAdtValue CreateAssociatedDeclarationShape(
        string caseName,
        string name,
        Utils.SourceSpan span,
        IReadOnlyList<Eidosc.Ast.Types.TypeParam> typeParameters,
        Symbol owner,
        MetaComptimeContext meta)
    {
        var ownerIdentity = CreateStableIdentity(owner, meta.SymbolTable);
        var declaration = new ComptimeDeclValue(
            SymbolId.None,
            Hash($"{ownerIdentity}|{caseName}|{name}|{span.Position}|{span.Length}"),
            name,
            ToCanonicalShapeKind(caseName),
            span)
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Declaration,
                WellKnownTypeIds.MetaDeclarationId)
        };
        var genericArguments = List(typeParameters.Select(parameter =>
            (ComptimeValue)CreateGenericArgumentShape(new MetaGenericArgumentRef(
                MetaGenericArgumentDomain.Type,
                parameter.Name,
                Hash($"{ownerIdentity}|{caseName}|{name}|generic|{parameter.Name}"),
                SymbolId.None,
                null))));
        return CreateDeclarationShapeCase(
            caseName,
            declaration,
            owner.IsPublic,
            genericArguments,
            List([]),
            generatedOrigin: null,
            meta);
    }

    private static ComptimeMetaObjectValue CreateOriginShape(
        GeneratedDeclarationOrigin origin,
        MetaComptimeContext meta) =>
        TypedShapeObject(
            "origin",
            WellKnownStrings.Meta.Types.Origin,
            WellKnownTypeIds.MetaOriginId,
            [
                ("identity", new ComptimeStringValue(origin.StableIdentity)),
                ("generationSlot", new ComptimeStringValue(origin.GenerationSlotIdentity)),
                ("generator", new ComptimeStringValue(origin.GeneratorIdentity)),
                ("target", new ComptimeStringValue(origin.TargetIdentity)),
                ("occurrence", new ComptimeStringValue(origin.ClauseOccurrenceIdentity)),
                ("outputIndex", new ComptimeIntegerValue(origin.ExpansionOutputIndex)),
                ("schemaVersion", new ComptimeIntegerValue(origin.MetaSchemaVersion)),
                ("span", CreateSpan(origin.ClauseSpan, meta.SymbolTable))
            ]);

    private static ComptimeMetaObjectValue TypedShapeObject(
        string schemaKind,
        string typeName,
        int typeId,
        IReadOnlyList<(string Name, ComptimeValue Value)> properties) =>
        Obj(schemaKind, properties.ToArray()) with
        {
            StaticType = MetaSchemaRegistry.MetaType(typeName, typeId)
        };

    private static ComptimeAdtValue CreateShapeCase(
        int ownerTypeId,
        string ownerTypeName,
        string caseName,
        ComptimeMetaObjectValue payload,
        SymbolTable symbolTable)
    {
        var owner = symbolTable.Symbols.Values
            .OfType<AdtSymbol>()
            .FirstOrDefault(symbol => symbol.TypeId.Value == ownerTypeId);
        var caseSymbol = owner?.DirectCases
            .Select(symbolTable.GetSymbol<AdtSymbol>)
            .FirstOrDefault(symbol => symbol != null && string.Equals(symbol.Name, caseName, StringComparison.Ordinal));
        return new ComptimeAdtValue(caseSymbol?.Id ?? SymbolId.None, caseName, [payload], [])
        {
            ConstructorIdentity = caseSymbol == null
                ? $"meta:{ownerTypeName}.{caseName}"
                : CreateStableIdentity(caseSymbol, symbolTable),
            StaticType = MetaSchemaRegistry.MetaType(ownerTypeName, ownerTypeId)
        };
    }

    private static string GetReferenceKind(MetaTypeKind kind) => kind switch
    {
        MetaTypeKind.Reference => "shared-borrow",
        MetaTypeKind.MutableReference => "mutable-borrow",
        MetaTypeKind.SharedReference => "shared-owner",
        MetaTypeKind.RawPointer => "raw-pointer",
        _ => "none"
    };

    private static string ToCanonicalShapeKind(string caseName)
    {
        var builder = new System.Text.StringBuilder(caseName.Length + 4);
        for (var index = 0; index < caseName.Length; index++)
        {
            var current = caseName[index];
            if (index > 0 && char.IsUpper(current)) builder.Append('-');
            builder.Append(char.ToLowerInvariant(current));
        }
        return builder.ToString();
    }
}
