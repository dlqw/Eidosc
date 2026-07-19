using System.Reflection;
using System.Text.Json;
using Eidosc.Hir;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;
using ConstructorTypeLayout = Eidosc.Mir.ConstructorTypeLayout;
using ParameterEffect = Eidosc.Mir.ParameterEffect;
using ParameterEffectMap = Eidosc.Mir.ParameterEffectMap;
using ReflectionType = System.Type;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ModuleHirStatePayloadTests
{
    [Fact]
    public void Create_CoversAndRestoresEveryConcreteHirShape()
    {
        var module = CreateAllShapesModule();
        var originalTypes = CollectHirObjectTypes(module);

        AssertConcreteCoverage(typeof(HirNode), originalTypes);
        AssertConcreteCoverage(typeof(HirStatement), originalTypes);
        AssertConcreteCoverage(typeof(HirPattern), originalTypes);

        var payload = ModuleHirStatePayload.Create(module);

        Assert.True(payload.IsRestorable, string.Join(Environment.NewLine, payload.UnsupportedNodeKinds));
        Assert.True(payload.TryRestore(out var restored));
        Assert.Equal(HirFormatter.FormatHir(module), HirFormatter.FormatHir(restored));

        var restoredTypes = CollectHirObjectTypes(restored);
        Assert.Empty(originalTypes.Except(restoredTypes).Select(static type => type.Name));
    }

    [Fact]
    public void Create_RoundTripsEveryConcreteHirShapeThroughJson()
    {
        var payload = ModuleHirStatePayload.Create(CreateAllShapesModule());

        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<ModuleHirStatePayload>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(payload.Hash, roundTripped!.Hash);
        Assert.True(roundTripped.TryRestore(out var restored));

        var restoredTypes = CollectHirObjectTypes(restored);
        AssertConcreteCoverage(typeof(HirNode), restoredTypes);
        AssertConcreteCoverage(typeof(HirStatement), restoredTypes);
        AssertConcreteCoverage(typeof(HirPattern), restoredTypes);
    }

    [Fact]
    public void Create_RestoresFunctionOwnershipContractThroughJson()
    {
        var valueType = Tid(BaseTypes.StringId);
        var sharedType = Tid(9001);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [valueType.Value] = new TypeDescriptor.Builtin(valueType.Value),
            [sharedType.Value] = new TypeDescriptor.Ref(valueType)
        };
        var contract = OwnershipContract.Create(
            Sid(41),
            "borrow_text",
            [("value", valueType)],
            sharedType,
            descriptors);
        var module = new HirModule
        {
            Name = "ownership_contract_restore",
            Declarations =
            [
                new HirFunc
                {
                    Name = "borrow_text",
                    SymbolId = Sid(41),
                    Parameters = [new HirParam { Name = "value", TypeId = valueType }],
                    ReturnType = sharedType,
                    OwnershipContract = contract
                }
            ]
        };

        var payload = ModuleHirStatePayload.Create(module);
        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<ModuleHirStatePayload>(json);

        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.TryRestore(out var restored));
        var restoredContract = Assert.IsType<HirFunc>(Assert.Single(restored.Declarations)).OwnershipContract;
        Assert.Equal(contract.CanonicalIdentity, restoredContract.CanonicalIdentity);
        Assert.Equal(OwnershipPassingKind.SharedBorrow, restoredContract.Result.Projection.Kind);
    }

    [Fact]
    public void Create_RoundTripsAttachedHirStateThroughJson()
    {
        var parameterEffects = new ParameterEffectMap();
        parameterEffects.Add("borrowValue", 0, [ParameterEffect.Read, ParameterEffect.Consume]);
        parameterEffects.Add("", 120, [ParameterEffect.Consume]);

        var copyLikeTypeIds = new HashSet<TypeId> { Tid(301), Tid(302) };
        var dynamicTypeKeys = new Dictionary<TypeId, string>
        {
            [Tid(401)] = "dyn:alpha",
            [Tid(402)] = "dyn:beta"
        };
        var typeDescriptors = new Dictionary<int, TypeDescriptor>
        {
            [1] = new TypeDescriptor.Builtin(1),
            [2] = new TypeDescriptor.Function([Tid(10), Tid(11)], Tid(12), "Console"),
            [3] = new TypeDescriptor.Tuple([Tid(13), Tid(14)]),
            [4] = new TypeDescriptor.TyCon(new TypeConstructorKey(TypeConstructorKeyKind.Symbol, 20), [Tid(15)])
            {
                ValueArgs =
                [
                    new GenericValueArgumentDescriptor(
                        0,
                        "typed:496e74:int:4",
                        "hash-4",
                        "4",
                        Tid(BaseTypes.IntId),
                        ReferencedParameterIndex: 0,
                        ValueVariableIndex: 7)
                ],
                EffectArgs =
                [
                    new GenericEffectArgumentDescriptor(1, "symbol:88", Tid(88))
                ]
            },
            [5] = new TypeDescriptor.Ref(Tid(16)),
            [6] = new TypeDescriptor.MutRef(Tid(17)),
            [7] = new TypeDescriptor.Shared(Tid(18)),
            [9] = new TypeDescriptor.TypeVar(3)
        };
        var constructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
        {
            [501] =
            [
                new ConstructorTypeLayout
                {
                    TypeName = "Option",
                    ConstructorName = "Some",
                    TagValue = 1,
                    RuntimeTypeId = 501,
                    FieldTypeIds = [Tid(601), Tid(602)]
                }
            ]
        };

        var payload = ModuleHirStatePayload.Create(
            CreateAllShapesModule(),
            parameterEffects,
            copyLikeTypeIds,
            dynamicTypeKeys,
            typeDescriptors,
            constructorLayouts);

        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<ModuleHirStatePayload>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(payload.Hash, roundTripped!.Hash);
        Assert.True(roundTripped.TryRestore(out var restoredModule, out var restoredState));
        Assert.Equal(HirFormatter.FormatHir(CreateAllShapesModule()), HirFormatter.FormatHir(restoredModule));

        Assert.True(restoredState.ParameterEffects.TryGetEffects("borrowValue", 0, out var nameEffects));
        Assert.Equal([ParameterEffect.Read, ParameterEffect.Consume], nameEffects);
        Assert.True(restoredState.ParameterEffects.TryGetEffects(null, 120, out var symbolEffects));
        Assert.Equal([ParameterEffect.Consume], symbolEffects);
        Assert.Equal(
            copyLikeTypeIds.Select(static id => id.Value).Order().ToArray(),
            restoredState.CopyLikeTypeIds.Select(static id => id.Value).Order().ToArray());
        Assert.Equal(
            dynamicTypeKeys.OrderBy(static entry => entry.Key.Value).Select(static entry => (entry.Key.Value, entry.Value)).ToArray(),
            restoredState.DynamicTypeKeys.OrderBy(static entry => entry.Key.Value).Select(static entry => (entry.Key.Value, entry.Value)).ToArray());

        foreach (var (typeId, descriptor) in typeDescriptors)
        {
            Assert.True(restoredState.TypeDescriptors.TryGetValue(typeId, out var restoredDescriptor));
            Assert.True(TypeDescriptorStructuralComparer.Instance.Equals(descriptor, restoredDescriptor));
        }

        var restoredLayout = Assert.Single(restoredState.ConstructorLayouts[501]);
        Assert.Equal("Option", restoredLayout.TypeName);
        Assert.Equal("Some", restoredLayout.ConstructorName);
        Assert.Equal(1u, restoredLayout.TagValue);
        Assert.Equal(501, restoredLayout.RuntimeTypeId);
        Assert.Equal([601, 602], restoredLayout.FieldTypeIds.Select(static id => id.Value).ToArray());
    }

    private static HirModule CreateAllShapesModule()
    {
        var allPatterns = CreateAllPatterns();
        var match = new HirMatch
        {
            Scrutinee = Var("scrutinee"),
            Branches = allPatterns
                .Select((pattern, index) => new HirMatchBranch
                {
                    Pattern = pattern,
                    Guard = index % 2 == 0 ? Bool(true) : null,
                    Body = Int(index)
                })
                .ToList(),
            IsExhaustive = true
        };

        var sequentialGuard = new HirSequentialGuard();
        sequentialGuard.Guards.Add(new HirPatternGuard
        {
            Pattern = new HirVarPattern { Name = "guarded", SymbolId = Sid(41), TypeId = Tid(41) },
            SourceExpression = Var("source")
        });
        sequentialGuard.Guards.Add(Bool(true));

        var allExpressions = new List<HirNode>
        {
            new HirError { Reason = "synthetic", IsRecovered = true },
            new HirCaseInject
            {
                Operand = Int(0) with { TypeId = Tid(701) },
                SourceCase = Sid(702),
                TargetAncestor = Sid(703),
                SourceTypeId = Tid(701),
                TypeId = Tid(704)
            },
            Int(1),
            new HirLiteral { LiteralKind = LiteralKind.Float, Value = 1.25d },
            new HirLiteral { LiteralKind = LiteralKind.String, Value = "text" },
            new HirLiteral { LiteralKind = LiteralKind.Char, Value = 'x' },
            Bool(false),
            Unit(),
            Var("value") with { TypeArgumentIds = [Tid(100)] },
            new HirConstGenericValue
            {
                Name = "N",
                SymbolId = Sid(101),
                ParameterIndex = 1,
                TypeId = Tid(BaseTypes.IntId)
            },
            new HirBinOp { Operator = BinaryOp.Add, Left = Int(1), Right = Int(2) },
            new HirUnaryOp { Operator = UnaryOp.Not, Operand = Bool(false) },
            new HirCall
            {
                Function = Var("callee"),
                Arguments = [Int(3), Var("arg")],
                Convention = CallConvention.Constructor,
                SurfaceSyntax = HirCallSurfaceSyntax.Method,
                OwnerSymbolId = Sid(60),
                OwnerPath = "Owner",
                HasExplicitOwner = true,
                ReceiverArgumentIndex = 0,
                InjectedArgumentCount = 1
            },
            new HirIf { Condition = Bool(true), ThenBranch = Int(1), ElseBranch = Int(0) },
            new HirLoop { Body = new HirBreak { Value = Int(5) } },
            new HirBreak(),
            new HirReturn { Value = Int(6) },
            new HirContinue(),
            new HirUnreachable(),
            new HirPatternGuard
            {
                Pattern = new HirVarPattern { Name = "guard", SymbolId = Sid(62), TypeId = Tid(62) },
                SourceExpression = Var("source")
            },
            sequentialGuard,
            match,
            new HirLambda
            {
                Parameters = [Param("lambdaParam", 63, 63)],
                ReturnType = Tid(64),
                Body = Var("lambdaParam"),
                Captures = [new HirCapture { Name = "captured", SymbolId = Sid(65), TypeId = Tid(65), IsMutable = true }]
            },
            new HirBlock
            {
                Statements =
                [
                    new HirDeclStatement
                    {
                        Declaration = new HirVal
                        {
                            Name = "local",
                            Pattern = new HirVarPattern { Name = "local", SymbolId = Sid(66), TypeId = Tid(66) },
                            Initializer = Int(7)
                        }
                    },
                    new HirExprStatement { Expression = Var("local") },
                    new HirAssignStatement { Target = Var("local"), Value = Int(8) }
                ],
                Result = Var("local")
            },
            new HirTuple { Elements = [Int(9), Var("tupleValue")] },
            new HirList { Elements = [Int(10), Var("listValue")], HasRest = true },
            new HirListComprehension
            {
                Output = Var("item"),
                Qualifiers =
                [
                    new HirQualifier
                    {
                        Kind = HirQualifierKind.Generator,
                        GeneratorPattern = new HirVarPattern { Name = "item", SymbolId = Sid(67), TypeId = Tid(67) },
                        GeneratorSource = Var("items")
                    },
                    new HirQualifier
                    {
                        Kind = HirQualifierKind.Guard,
                        GuardExpression = Bool(true)
                    }
                ]
            },
            new HirFieldAccess { Target = Var("record"), FieldName = "field", FieldSymbolId = Sid(68) },
            new HirIndexAccess { Target = Var("items"), Index = Int(0), TargetKind = HirIndexAccessKind.Aggregate }
        };

        return new HirModule
        {
            Name = "Synthetic",
            Path = ["Synthetic"],
            Exports = [Sid(1)],
            Imports =
            [
                new HirImport
                {
                    Path = ["Std", "Text"],
                    Alias = "Text",
                    SelectiveImports = ["String"],
                    IsUse = true
                }
            ],
            LinkLibraries = ["m"],
            Declarations =
            [
                new HirFunc
                {
                    Name = "allExpressions",
                    SourceName = "allExpressions",
                    IsModuleLevel = true,
                    SymbolId = Sid(1),
                    TypeId = Tid(1),
                    TypeParams =
                    [
                        new HirTypeParam
                        {
                            Name = "T",
                            SymbolId = Sid(2),
                            TypeId = Tid(2),
                            KindAnnotation = "kind2",
                            IsComptime = true,
                            ComptimeTypeAnnotation = "Type",
                            Constraints =
                            [
                                new HirTraitConstraint
                                {
                                    SymbolId = Sid(3),
                                    Name = "Show",
                                    ModulePath = ["Std", "Text"],
                                    TypeArgs = [new HirTypeArg { TypeId = Tid(4), DisplayText = "T" }]
                                }
                            ]
                        }
                    ],
                    Parameters = [Param("input", 5, 5)],
                    ReturnType = Tid(6),
                    RequiredAbilities = [Sid(7)],
                    IsEntry = true,
                    Body = new HirTuple { Elements = allExpressions }
                },
                new HirVal
                {
                    Name = "topValue",
                    IsModuleLevel = true,
                    SymbolId = Sid(9),
                    TypeId = Tid(9),
                    Pattern = new HirAsPattern
                    {
                        InnerPattern = new HirVarPattern { Name = "topValue", SymbolId = Sid(10), TypeId = Tid(10) },
                        Name = "alias",
                        SymbolId = Sid(11),
                        BindingMode = PatternBindingMode.ByValue,
                        IsMutableBinding = true
                    },
                    TypeAnnotation = Tid(12),
                    Initializer = new HirList { Elements = [Int(11)], HasRest = false },
                    IsComptime = true
                },
                new HirVarDecl
                {
                    Name = "topMutable",
                    IsModuleLevel = true,
                    SymbolId = Sid(13),
                    TypeId = Tid(13),
                    Pattern = new HirVarPattern
                    {
                        Name = "topMutable",
                        SymbolId = Sid(14),
                        TypeId = Tid(14),
                        BindingMode = PatternBindingMode.MutableBorrow,
                        IsMutableBinding = true
                    },
                    TypeAnnotation = Tid(15),
                    Initializer = Int(12)
                },
                new HirAdt
                {
                    Name = "Box",
                    IsModuleLevel = true,
                    SymbolId = Sid(16),
                    TypeId = Tid(16),
                    TypeParams = [new HirTypeParam { Name = "A", SymbolId = Sid(17), TypeId = Tid(17) }],
                    Constructors =
                    [
                        new HirCtor
                        {
                            Name = "Box",
                            SymbolId = Sid(18),
                            Fields = [new HirField { Name = "value", SymbolId = Sid(19), TypeId = Tid(19) }]
                        }
                    ],
                    AliasTarget = Tid(20),
                    IsRecord = true
                },
                new HirEffect
                {
                    Name = "Console",
                    IsModuleLevel = true,
                    SymbolId = Sid(21),
                    TypeId = Tid(21)
                },
                new HirTrait
                {
                    Name = "Show",
                    IsModuleLevel = true,
                    SymbolId = Sid(27),
                    TypeId = Tid(27),
                    TypeParams = [new HirTypeParam { Name = "S", SymbolId = Sid(28), TypeId = Tid(28) }],
                    AssociatedTypes = [new HirAssocType { Name = "Output", SymbolId = Sid(29), DefaultType = Tid(29) }],
                    Methods =
                    [
                        new HirFunc
                        {
                            Name = "show",
                            SourceName = "show",
                            SymbolId = Sid(30),
                            TypeId = Tid(30),
                            Parameters = [Param("value", 31, 31)],
                            ReturnType = Tid(32)
                        }
                    ],
                    SuperTraits = [Sid(33)]
                },
                new HirImpl
                {
                    Name = "ShowBox",
                    IsModuleLevel = true,
                    SymbolId = Sid(34),
                    TypeId = Tid(34),
                    TraitId = Sid(27),
                    ImplementingType = Tid(16),
                    Methods =
                    [
                        new HirFunc
                        {
                            Name = "showBox",
                            SourceName = "showBox",
                            SymbolId = Sid(35),
                            TypeId = Tid(35),
                            Parameters = [Param("box", 36, 36)],
                            ReturnType = Tid(37),
                            Body = new HirLiteral { LiteralKind = LiteralKind.String, Value = "box" }
                        }
                    ]
                },
                new HirTypeAlias
                {
                    Name = "Alias",
                    IsModuleLevel = true,
                    SymbolId = Sid(38),
                    TypeId = Tid(38),
                    TypeParams = [new HirTypeParam { Name = "A", SymbolId = Sid(39), TypeId = Tid(39) }],
                    TargetType = Tid(40)
                }
            ]
        };
    }

    private static List<HirPattern> CreateAllPatterns()
    {
        var varPattern = new HirVarPattern
        {
            Name = "value",
            SymbolId = Sid(100),
            TypeId = Tid(100),
            BindingMode = PatternBindingMode.SharedBorrow
        };
        var literalPattern = new HirLiteralPattern { Value = 1, TypeId = Tid(101) };

        return
        [
            new HirErrorPattern { Reason = "synthetic", IsRecovered = true, TypeId = Tid(102) },
            varPattern,
            literalPattern,
            new HirCtorPattern
            {
                ConstructorName = "Some",
                ConstructorSymbolId = Sid(103),
                TypeId = Tid(103),
                Fields = [new HirFieldPattern { FieldName = "value", Pattern = varPattern }]
            },
            new HirTuplePattern { TypeId = Tid(104), Elements = [varPattern, literalPattern] },
            new HirListPattern
            {
                TypeId = Tid(105),
                Elements = [varPattern],
                HasRest = true,
                RestPattern = new HirVarPattern { Name = "rest", SymbolId = Sid(105), TypeId = Tid(105) },
                SuffixElements = [literalPattern]
            },
            new HirOrPattern { TypeId = Tid(106), Left = literalPattern, Right = varPattern },
            new HirAndPattern { TypeId = Tid(107), Left = literalPattern, Right = varPattern },
            new HirNotPattern { TypeId = Tid(108), InnerPattern = varPattern },
            new HirRangePattern
            {
                TypeId = Tid(109),
                Start = new HirLiteralPattern { Value = 1, TypeId = Tid(110) },
                End = new HirLiteralPattern { Value = 3, TypeId = Tid(111) }
            },
            new HirViewPattern
            {
                TypeId = Tid(112),
                View = Var("view"),
                ViewResultTypeId = Tid(113),
                InnerPattern = varPattern
            },
            new HirAsPattern
            {
                TypeId = Tid(114),
                InnerPattern = varPattern,
                Name = "named",
                SymbolId = Sid(114),
                BindingMode = PatternBindingMode.ByValue,
                IsMutableBinding = true
            }
        ];
    }

    private static void AssertConcreteCoverage(ReflectionType baseType, IReadOnlySet<ReflectionType> observedTypes)
    {
        var missing = baseType.Assembly.GetTypes()
            .Where(type => !type.IsAbstract && baseType.IsAssignableFrom(type))
            .Where(type => !observedTypes.Contains(type))
            .Select(static type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, $"Missing synthetic HIR coverage for {baseType.Name}: {string.Join(", ", missing)}");
    }

    private static IReadOnlySet<ReflectionType> CollectHirObjectTypes(object root)
    {
        var result = new SortedSet<ReflectionType>(Comparer<ReflectionType>.Create(static (left, right) =>
            string.CompareOrdinal(left.FullName, right.FullName)));
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(root);
        return result;

        void Visit(object? value)
        {
            if (value == null || value is string)
            {
                return;
            }

            if (value is System.Collections.IEnumerable sequence && value.GetType().Namespace != "Eidosc.Hir")
            {
                foreach (var item in sequence)
                {
                    Visit(item);
                }

                return;
            }

            var type = value.GetType();
            if (type.Namespace != "Eidosc.Hir" || type.IsEnum || !seen.Add(value))
            {
                return;
            }

            result.Add(type);
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length == 0)
                {
                    Visit(property.GetValue(value));
                }
            }
        }
    }

    private static HirParam Param(string name, int symbolId, int typeId) =>
        new() { Name = name, SymbolId = Sid(symbolId), TypeId = Tid(typeId), IsMutable = true };

    private static HirVar Var(string name) =>
        new() { Name = name, SymbolId = Sid(Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % 1000), TypeId = Tid(200) };

    private static HirLiteral Int(int value) =>
        new() { LiteralKind = LiteralKind.Int, Value = value, TypeId = Tid(201) };

    private static HirLiteral Bool(bool value) =>
        new() { LiteralKind = LiteralKind.Bool, Value = value, TypeId = Tid(202) };

    private static HirLiteral Unit() =>
        new() { LiteralKind = LiteralKind.Unit, Value = null, TypeId = Tid(203) };

    private static SymbolId Sid(int value) => new(value);

    private static TypeId Tid(int value) => new(value);
}
