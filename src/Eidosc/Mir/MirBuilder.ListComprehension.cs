using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

public sealed partial class MirBuilder
{
    private sealed record ComprehensionGeneratorInfo(
        HirVarPattern Pattern,
        HirNode Source,
        int? SourceLength,
        TypeId ElementType,
        SourceSpan Span);

    private readonly record struct ComprehensionBindingScope(
        bool NameBound,
        string? Name,
        LocalId PreviousNameLocal,
        bool SymbolBound,
        SymbolId SymbolId,
        LocalId PreviousSymbolLocal);

    private sealed class ListComprehensionLoweringContext
    {
        public required HirListComprehension Comprehension { get; init; }
        public required Dictionary<int, ComprehensionGeneratorInfo> Generators { get; init; }
        public required MirPlace ResultPlace { get; init; }
    }

    private MirOperand ConvertListComprehension(HirListComprehension comprehension)
    {
        var generators = new Dictionary<int, ComprehensionGeneratorInfo>();
        var maxResultLength = 1L;
        var allSourceLengthsKnown = true;

        for (var i = 0; i < comprehension.Qualifiers.Count; i++)
        {
            var qualifier = comprehension.Qualifiers[i];
            if (qualifier.Kind != HirQualifierKind.Generator)
            {
                continue;
            }

            if (qualifier.GeneratorPattern is not HirVarPattern varPattern)
            {
                return LowerListComprehensionUnsupportedGeneratorPattern(
                    comprehension,
                    qualifier.Span,
                    qualifier.GeneratorPattern);
            }

            if (qualifier.GeneratorSource == null)
            {
                return LowerListComprehensionAsPoison(
                    comprehension,
                    qualifier.Span,
                    DiagnosticMessages.ListComprehensionMissingGeneratorSourceReason);
            }

            var sourceLength = TryGetStaticComprehensionSourceLength(qualifier.GeneratorSource);
            if (sourceLength.HasValue)
            {
                maxResultLength = Math.Min(int.MaxValue, maxResultLength * Math.Max(sourceLength.Value, 0));
            }
            else
            {
                allSourceLengthsKnown = false;
            }

            var elementType = InferComprehensionElementType(varPattern, qualifier.GeneratorSource);

            generators[i] = new ComprehensionGeneratorInfo(
                varPattern,
                qualifier.GeneratorSource,
                sourceLength,
                elementType,
                qualifier.Span);
        }

        if (generators.Count == 0)
        {
            return LowerListComprehensionAsPoison(
                comprehension,
                comprehension.Span,
                DiagnosticMessages.ListComprehensionGeneratorRequiredReason);
        }

        var hasGuardQualifier = comprehension.Qualifiers.Any(q => q.Kind == HirQualifierKind.Guard);
        var boundedCapacity = maxResultLength < 0 ? 0L : Math.Min(maxResultLength, int.MaxValue);
        var initialCapacity = allSourceLengthsKnown ? (int)boundedCapacity : 8;
        var resultElementType = comprehension.Output?.TypeId ?? TypeId.None;
        var resultElementSize = GetRuntimeElementSize(resultElementType);
        var resultPlace = EmitRuntimeArrayNew(
            comprehension.TypeId,
            initialCapacity,
            resultElementSize,
            comprehension.Span);

        if (allSourceLengthsKnown && !hasGuardQualifier)
        {
            RegisterKnownListLength(resultPlace, initialCapacity);
        }
        else
        {
            ClearKnownListLength(resultPlace);
        }

        var exitBlock = NewBlock();
        _currentFunc!.BasicBlocks.Add(exitBlock);

        var loweringContext = new ListComprehensionLoweringContext
        {
            Comprehension = comprehension,
            Generators = generators,
            ResultPlace = resultPlace
        };

        LowerComprehensionQualifierSequence(loweringContext, qualifierIndex: 0, continueTarget: exitBlock.Id);
        _currentBlock = exitBlock;
        return resultPlace;
    }

    private MirOperand LowerListComprehensionAsPoison(
        HirListComprehension comprehension,
        SourceSpan span,
        string reason)
    {
        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.ListComprehensionMirPoison(reason),
            "E5101");
        if (HasSpan(span))
        {
            diagnostic.WithLabel(span, DiagnosticMessages.ListComprehensionLoweringPoisonLabel);
        }

        diagnostic.WithHelp(DiagnosticMessages.ListComprehensionVarPatternGeneratorHelp);
        Diagnostics.Add(diagnostic);

        return CreatePoisonOperand(
            comprehension.TypeId,
            comprehension.Span,
            DiagnosticMessages.ListComprehensionLoweringFailedReason(reason));
    }

    private MirOperand LowerListComprehensionUnsupportedGeneratorPattern(
        HirListComprehension comprehension,
        SourceSpan span,
        HirPattern? pattern)
    {
        var patternKind = pattern?.GetType().Name ?? "null";
        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.ListComprehensionUnsupportedGeneratorPattern(patternKind),
                "E5101")
            .WithMetadata("phase", "mir")
            .WithMetadata("feature", "list-comprehension")
            .WithMetadata("qualifier", "generator")
            .WithMetadata("supportedPattern", nameof(HirVarPattern))
            .WithMetadata("actualPattern", patternKind)
            .WithMetadata("reason", "unsupported-generator-pattern");

        if (HasSpan(span))
        {
            diagnostic.WithLabel(span, DiagnosticMessages.ListComprehensionUnsupportedGeneratorPatternLabel);
        }

        diagnostic.WithHelp(DiagnosticMessages.ListComprehensionUnsupportedGeneratorPatternHelp);
        Diagnostics.Add(diagnostic);

        return CreatePoisonOperand(
            comprehension.TypeId,
            comprehension.Span,
            DiagnosticMessages.ListComprehensionUnsupportedGeneratorPatternReason(patternKind));
    }

    private static TypeId InferComprehensionElementType(HirVarPattern pattern, HirNode source)
    {
        if (pattern.TypeId.IsValid && BaseTypes.IsBuiltIn(pattern.TypeId))
        {
            return pattern.TypeId;
        }

        if (source is HirList { Elements.Count: > 0 } sourceList)
        {
            foreach (var element in sourceList.Elements)
            {
                if (element.TypeId.IsValid)
                {
                    return element.TypeId;
                }

                if (element is HirLiteral literal)
                {
                    return literal.LiteralKind switch
                    {
                        LiteralKind.Int => new TypeId(BaseTypes.IntId),
                        LiteralKind.Float => new TypeId(BaseTypes.FloatId),
                        LiteralKind.String => new TypeId(BaseTypes.StringId),
                        LiteralKind.Char => new TypeId(BaseTypes.CharId),
                        LiteralKind.Bool => new TypeId(BaseTypes.BoolId),
                        LiteralKind.Unit => new TypeId(BaseTypes.UnitId),
                        _ => TypeId.None
                    };
                }
            }
        }

        if (source is HirListComprehension { Output.TypeId.IsValid: true } sourceComprehension)
        {
            return sourceComprehension.Output.TypeId;
        }

        if (pattern.TypeId.IsValid)
        {
            return pattern.TypeId;
        }

        return TypeId.None;
    }

    private static int? TryGetStaticComprehensionSourceLength(HirNode source)
    {
        return source switch
        {
            HirList list => list.Elements.Count,
            _ => null
        };
    }

    private void LowerComprehensionQualifierSequence(
        ListComprehensionLoweringContext context,
        int qualifierIndex,
        BlockId continueTarget)
    {
        if (qualifierIndex >= context.Comprehension.Qualifiers.Count)
        {
            EmitComprehensionAppend(context);
            _currentBlock!.Terminator = new MirGoto
            {
                Target = continueTarget,
                Span = context.Comprehension.Span
            };
            return;
        }

        var qualifier = context.Comprehension.Qualifiers[qualifierIndex];
        if (qualifier.Kind == HirQualifierKind.Guard)
        {
            LowerComprehensionGuardQualifier(context, qualifier, qualifierIndex, continueTarget);
            return;
        }

        LowerComprehensionGeneratorQualifier(context, qualifier, qualifierIndex, continueTarget);
    }

    private void LowerComprehensionGuardQualifier(
        ListComprehensionLoweringContext context,
        HirQualifier qualifier,
        int qualifierIndex,
        BlockId continueTarget)
    {
        if (qualifier.GuardExpression == null)
        {
            _currentBlock!.Terminator = new MirGoto
            {
                Target = continueTarget,
                Span = qualifier.Span
            };
            return;
        }

        var guardValue = ConvertExpr(qualifier.GuardExpression);
        guardValue = EnsureReadValue(guardValue, qualifier.GuardExpression.TypeId, qualifier.Span);

        var passBlock = NewBlock();
        _currentFunc!.BasicBlocks.Add(passBlock);

        _currentBlock!.Terminator = new MirSwitch
        {
            Discriminant = guardValue,
            Branches =
            [
                new MirSwitchBranch
                {
                    Value = CreateBoolConstant(true, qualifier.Span),
                    Target = passBlock.Id
                }
            ],
            DefaultTarget = continueTarget,
            Span = qualifier.Span
        };

        _currentBlock = passBlock;
        LowerComprehensionQualifierSequence(context, qualifierIndex + 1, continueTarget);
    }

    private void LowerComprehensionGeneratorQualifier(
        ListComprehensionLoweringContext context,
        HirQualifier qualifier,
        int qualifierIndex,
        BlockId continueTarget)
    {
        if (!context.Generators.TryGetValue(qualifierIndex, out var generator))
        {
            _currentBlock!.Terminator = new MirGoto
            {
                Target = continueTarget,
                Span = qualifier.Span
            };
            return;
        }

        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var sourceOperand = ConvertExpr(generator.Source);
        var sourcePlace = EnsurePlaceOperand(sourceOperand, generator.Source.TypeId, qualifier.Span);

        var indexLocal = NewLocal($"$lc_i{qualifierIndex}", intType, isMutable: true);
        var indexPlace = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = indexLocal,
            TypeId = intType,
            Span = qualifier.Span
        };
        EmitInitialization(indexPlace, CreateIntConstant(0, qualifier.Span), qualifier.Span);

        var headerBlock = NewBlock();
        var bodyBlock = NewBlock();
        var incrementBlock = NewBlock();
        var exitBlock = NewBlock();
        _currentFunc!.BasicBlocks.Add(headerBlock);
        _currentFunc.BasicBlocks.Add(bodyBlock);
        _currentFunc.BasicBlocks.Add(incrementBlock);
        _currentFunc.BasicBlocks.Add(exitBlock);

        _currentBlock!.Terminator = new MirGoto
        {
            Target = headerBlock.Id,
            Span = qualifier.Span
        };

        _currentBlock = headerBlock;
        var indexValue = EnsureReadValue(indexPlace, intType, qualifier.Span);
        MirOperand sourceLengthValue = generator.SourceLength.HasValue
            ? CreateIntConstant(generator.SourceLength.Value, qualifier.Span)
            : EmitRuntimeArrayLength(sourcePlace, qualifier.Span);
        sourceLengthValue = EnsureReadValue(sourceLengthValue, intType, qualifier.Span);
        var loopCondition = NewTemp(boolType);
        _currentBlock.Instructions.Add(new MirBinOp
        {
            Target = loopCondition,
            Operator = BinaryOp.Lt,
            Left = indexValue,
            Right = sourceLengthValue,
            Span = qualifier.Span
        });
        _currentBlock.Terminator = new MirSwitch
        {
            Discriminant = loopCondition,
            Branches =
            [
                new MirSwitchBranch
                {
                    Value = CreateBoolConstant(true, qualifier.Span),
                    Target = bodyBlock.Id
                }
            ],
            DefaultTarget = exitBlock.Id,
            Span = qualifier.Span
        };

        _currentBlock = bodyBlock;
        var indexForLoad = EnsureReadValue(indexPlace, intType, qualifier.Span);
        var sourceSlot = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = sourcePlace,
            Index = indexForLoad,
            IndexAccessKind = MirIndexAccessKind.RuntimeArray,
            TypeId = generator.ElementType,
            Span = qualifier.Span
        };
        var currentElement = NewTemp(generator.ElementType);
        _currentBlock.Instructions.Add(new MirLoad
        {
            Target = currentElement,
            Source = sourceSlot,
            CreatesBorrowAlias = false,
            Span = qualifier.Span
        });
        _comprehensionElementLocals.Add(currentElement.Local);

        var bindingScope = PushComprehensionBinding(generator.Pattern, currentElement);
        LowerComprehensionQualifierSequence(context, qualifierIndex + 1, incrementBlock.Id);
        PopComprehensionBinding(bindingScope);

        _currentBlock = incrementBlock;
        var currentIndex = EnsureReadValue(indexPlace, intType, qualifier.Span);
        var nextIndex = NewTemp(intType);
        _currentBlock.Instructions.Add(new MirBinOp
        {
            Target = nextIndex,
            Operator = BinaryOp.Add,
            Left = currentIndex,
            Right = CreateIntConstant(1, qualifier.Span),
            Span = qualifier.Span
        });
        EmitStore(indexPlace, nextIndex, qualifier.Span);
        _currentBlock.Terminator = new MirGoto
        {
            Target = headerBlock.Id,
            Span = qualifier.Span
        };

        _currentBlock = exitBlock;
        _currentBlock.Terminator = new MirGoto
        {
            Target = continueTarget,
            Span = qualifier.Span
        };
    }

    private void EmitComprehensionAppend(ListComprehensionLoweringContext context)
    {
        var outputNode = context.Comprehension.Output;
        var outputValue = ConvertExpr(outputNode);
        var pushValue = PrepareCallArgument(outputValue, outputNode.TypeId, outputNode.Span);
        var elementType = outputNode.TypeId.IsValid ? outputNode.TypeId : outputValue.TypeId;
        var elementSize = GetRuntimeElementSize(elementType);

        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = context.ResultPlace,
            Function = MirRuntimeFunctions.CreateFunctionRef(
                WellKnownStrings.InternalNames.ArrayPush,
                context.ResultPlace.TypeId,
                outputNode.Span),
            Arguments =
            [
                context.ResultPlace,
                pushValue,
                CreateIntConstant(elementSize, outputNode.Span)
            ],
            Span = outputNode.Span
        });
        RegisterRuntimeArrayLocal(context.ResultPlace);
    }

    private ComprehensionBindingScope PushComprehensionBinding(HirVarPattern pattern, MirPlace currentElement)
    {
        var nameBound = false;
        var previousNameLocal = LocalId.None;
        if (!pattern.IsWildcard &&
            !string.IsNullOrEmpty(pattern.Name) &&
            pattern.Name != "_")
        {
            nameBound = true;
            _variableLocals.TryGetValue(pattern.Name, out previousNameLocal);
            _variableLocals[pattern.Name] = currentElement.Local;
        }

        var symbolBound = false;
        var previousSymbolLocal = LocalId.None;
        if (pattern.SymbolId.IsValid)
        {
            symbolBound = true;
            _symbolLocals.TryGetValue(pattern.SymbolId, out previousSymbolLocal);
            _symbolLocals[pattern.SymbolId] = currentElement.Local;
        }

        return new ComprehensionBindingScope(
            nameBound,
            pattern.Name,
            previousNameLocal,
            symbolBound,
            pattern.SymbolId,
            previousSymbolLocal);
    }

    private void PopComprehensionBinding(ComprehensionBindingScope scope)
    {
        if (scope.NameBound && !string.IsNullOrEmpty(scope.Name))
        {
            if (scope.PreviousNameLocal.IsValid)
            {
                _variableLocals[scope.Name] = scope.PreviousNameLocal;
            }
            else
            {
                _variableLocals.Remove(scope.Name);
            }
        }

        if (scope.SymbolBound && scope.SymbolId.IsValid)
        {
            if (scope.PreviousSymbolLocal.IsValid)
            {
                _symbolLocals[scope.SymbolId] = scope.PreviousSymbolLocal;
            }
            else
            {
                _symbolLocals.Remove(scope.SymbolId);
            }
        }
    }
}
