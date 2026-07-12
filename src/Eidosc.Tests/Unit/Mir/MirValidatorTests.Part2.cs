using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed partial class MirValidatorTests
{
    [Fact]
    public void Validate_MoveWithNonLocalTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirMove
                {
                    Target = CreateFieldPlace(intType),
                    Source = CreateLocalPlace(2, intType)
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR move target place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MoveWithNonLocalSource_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirMove
                {
                    Target = CreateLocalPlace(2, intType),
                    Source = CreateFieldPlace(intType)
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR move source place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NameOnlyFunctionRef_ReportsMissingFunctionIdentity()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirCall
                {
                    Target = CreateLocalPlace(1, intType),
                    Function = new MirFunctionRef
                    {
                        Name = "callee",
                        TypeId = intType
                    },
                    Arguments = []
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.MissingFunctionIdentityCode, diagnostic.Code);
        Assert.Contains("callee", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("structured FunctionId", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RuntimeFunctionRefWithRuntimeIdentity_Succeeds()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var rawPtrType = new TypeId(BaseTypes.RawPtrId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirCall
                {
                    Target = CreateLocalPlace(1, rawPtrType),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        "array_push",
                        rawPtrType,
                        Eidosc.Utils.SourceSpan.Empty),
                    Arguments =
                    [
                        CreateLocalPlace(2, rawPtrType),
                        CreateLocalPlace(3, rawPtrType),
                        new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(8)
                        }
                    ]
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.True(validator.Validate(module));
        Assert.Empty(validator.Diagnostics);
    }

    [Fact]
    public void Validate_DropOpenTypeVariable_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var openType = new TypeId(100);
        var module = new MirModule
        {
            Name = "Main",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [openType.Value] = new TypeDescriptor.TypeVar(1)
            },
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
                    EntryBlockId = new BlockId { Value = 1 },
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions =
                            [
                                new MirDrop
                                {
                                    Value = CreateLocalPlace(1, openType)
                                }
                            ],
                            Terminator = CreateReturnZero(intType)
                        }
                    ]
                }
            ]
        };

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.InvalidDropCode, diagnostic.Code);
        Assert.Contains("open type variable", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DropAfterMove_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirMove
                {
                    Target = CreateLocalPlace(2, stringType),
                    Source = CreateLocalPlace(1, stringType)
                },
                new MirDrop
                {
                    Value = CreateLocalPlace(1, stringType)
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.DropAfterMoveCode, diagnostic.Code);
        Assert.Contains("ownership has moved", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DropAfterMoveAcrossSuccessorBlock_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = stringType,
                    EntryBlockId = new BlockId { Value = 1 },
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions =
                            [
                                new MirMove
                                {
                                    Target = CreateLocalPlace(2, stringType),
                                    Source = CreateLocalPlace(1, stringType)
                                }
                            ],
                            Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
                        },
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 2 },
                            Instructions =
                            [
                                new MirDrop
                                {
                                    Value = CreateLocalPlace(1, stringType)
                                }
                            ],
                            Terminator = CreateReturnZero(intType)
                        }
                    ]
                }
            ]
        };

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.DropAfterMoveCode, diagnostic.Code);
        Assert.Contains("ownership has moved", diagnostic.Message, StringComparison.Ordinal);
    }

    private static MirModule CreateSingleBlockModule(
        TypeId returnType,
        List<MirInstruction> instructions,
        MirTerminator terminator)
    {
        return new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = returnType,
                    EntryBlockId = new BlockId { Value = 1 },
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions = instructions,
                            Terminator = terminator
                        }
                    ]
                }
            ]
        };
    }

    private static MirPlace CreateLocalPlace(int localId, TypeId typeId)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = localId },
            TypeId = typeId
        };
    }

    private static MirPlace CreateFieldPlace(TypeId typeId)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = CreateLocalPlace(1, typeId),
            FieldName = "0",
            TypeId = typeId
        };
    }

    private static MirReturn CreateReturnZero(TypeId typeId)
    {
        return new MirReturn
        {
            Value = new MirConstant
            {
                TypeId = typeId,
                Value = new MirConstantValue.IntValue(0)
            }
        };
    }

    private sealed record UnsupportedInstruction : MirInstruction;

    private sealed record UnsupportedTerminator : MirTerminator;
}
