using Eidosc.Symbols;
using Eidosc;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed partial class MirGenericSpecializerTests
{
    [Fact]
    public void Run_BuiltinEqLowering_UsesStructuredEqualityRole()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var traitMethodSymbol = new SymbolId(1877);
        var callerSymbol = new SymbolId(1878);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Eq", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "eq",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [intType, intType],
            ReturnType = boolType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0, 1],
            CompilerSemanticRole = CompilerSemanticRole.Equality
        });

        var left = LocalPlace(1, intType);
        var right = LocalPlace(2, intType);
        var result = LocalPlace(3, boolType);
        var caller = BuildFunction(
            returnType: boolType,
            locals:
            [
                new MirLocal { Id = left.Local, Name = "left", TypeId = intType, IsParameter = true },
                new MirLocal { Id = right.Local, Name = "right", TypeId = intType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = boolType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "eq",
                        SymbolId = traitMethodSymbol,
                        TypeId = boolType,
                        CompilerSemanticRole = CompilerSemanticRole.Equality
                    },
                    Arguments = [left, right]
                }
            ],
            returnValue: result,
            name: "caller_structured_eq",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "builtin_eq_uses_structured_role",
            Functions = [caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewritten = Assert.IsType<MirBinOp>(Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions));
        Assert.Equal(BinaryOp.Eq, rewritten.Operator);
    }

    [Fact]
    public void Run_TraitMethodRewrite_UpdatesSignatureTypeIdToImplMethodSignature()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var selfType = new TypeId(1881);
        var traitSignatureType = new TypeId(1882);
        var traitMethodSymbol = new SymbolId(1883);
        var implMethodSymbol = new SymbolId(1884);
        var callerSymbol = new SymbolId(1885);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Display", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "display",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [selfType],
            ReturnType = stringType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0]
        });

        var implId = symbolTable.DeclareImpl(traitId, intType, SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "display_int",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            ParamTypes = [intType],
            ReturnType = stringType
        });
        symbolTable.AddMethodToImpl(implId, implMethodSymbol, traitMethodSymbol);

        var argument = LocalPlace(1, intType);
        var result = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = argument.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "display",
                        SymbolId = traitMethodSymbol,
                        TypeId = stringType,
                        SignatureTypeId = traitSignatureType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [argument]
                }
            ],
            returnValue: result,
            name: "caller_display_int",
            symbolId: callerSymbol);

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = argument.Local, Name = "value", TypeId = intType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("1")
            },
            name: "display_int",
            symbolId: implMethodSymbol);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "trait_rewrite_signature_type_id",
            Functions = [caller, implMethod],
            TraitImpls = MirTraitImplsFrom(symbolTable),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [selfType.Value] = new TypeDescriptor.TypeVar(0),
                [traitSignatureType.Value] = new TypeDescriptor.Function([selfType], stringType)
            }
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.NotEqual(traitSignatureType, rewrittenRef.SignatureTypeId);

        var signature = Assert.IsType<TypeDescriptor.Function>(
            specialized.TypeDescriptors[rewrittenRef.SignatureTypeId.Value]);
        Assert.Equal([intType], signature.ParamTypes);
        Assert.Equal(stringType, signature.ReturnType);
    }

    [Fact]
    public void Run_TraitDispatchWithoutConcreteDispatchType_RecordsFailureOnTraitOnlyPath()
    {
        var traitId = new SymbolId(2011);
        var callerSymbol = new SymbolId(2012);
        var typeVariable = new TypeId(2013);
        var stringType = new TypeId(BaseTypes.StringId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var value = LocalPlace(1, typeVariable);
        var result = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = value.Local, Name = "value", TypeId = typeVariable },
                new MirLocal { Id = result.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "display",
                        SymbolId = SymbolId.None,
                        TypeId = stringType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [value]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_no_concrete_dispatch_type",
            symbolId: callerSymbol);

        var specializer = new MirGenericSpecializer();
        var specialized = specializer.Run(new MirModule
        {
            Name = "trait_dispatch_no_concrete_type",
            Functions = [caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0)
            }
        });

        var failure = Assert.Single(specialized.SpecializationFailures);
        Assert.Equal("no-concrete-dispatch-type", failure.Reason);
        Assert.Equal("trait:2011:display", failure.TemplateKey);
        Assert.StartsWith("trait-dispatch:", failure.SignatureKey, StringComparison.Ordinal);

        var diagnostic = Assert.Single(specializer.Diagnostics, diagnostic => diagnostic.Code == "E5310");
        Assert.Equal("mir-specialization", diagnostic.Metadata["phase"]);
        Assert.Equal("no-concrete-dispatch-type", diagnostic.Metadata["reason"]);
        Assert.Equal(failure.TemplateKey, diagnostic.Metadata["templateKey"]);
        Assert.Equal("display", diagnostic.Metadata["templateName"]);
        Assert.Equal(failure.SignatureKey, diagnostic.Metadata["signatureKey"]);
    }

    [Fact]
    public void Run_OrdinaryCompilerSemanticRole_DoesNotBecomeTraitDispatchIdentity()
    {
        var typeVariable = new TypeId(2021);
        var unitType = new TypeId(BaseTypes.UnitId);
        var appendSymbol = new SymbolId(2022);
        var callerSymbol = new SymbolId(2023);
        var left = LocalPlace(1, typeVariable);
        var right = LocalPlace(2, typeVariable);
        var result = LocalPlace(3, typeVariable);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = left.Local, Name = "left", TypeId = typeVariable },
                new MirLocal { Id = right.Local, Name = "right", TypeId = typeVariable },
                new MirLocal { Id = result.Local, Name = "result", TypeId = typeVariable }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "append",
                        SymbolId = appendSymbol,
                        TypeId = typeVariable,
                        CompilerSemanticRole = CompilerSemanticRole.SemigroupAppend
                    },
                    Arguments = [left, right]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_ordinary_semantic_role",
            symbolId: callerSymbol);

        var specializer = new MirGenericSpecializer();
        var specialized = specializer.Run(new MirModule
        {
            Name = "ordinary_semantic_role",
            Functions = [caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0)
            }
        });

        Assert.Empty(specialized.SpecializationFailures);
        Assert.DoesNotContain(specializer.Diagnostics, diagnostic => diagnostic.Code == "E5310");
        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(CompilerSemanticRole.SemigroupAppend, rewrittenRef.CompilerSemanticRole);
        Assert.False(rewrittenRef.TraitOwnerId.IsValid);
        Assert.Equal(SelfPosition.Unknown, rewrittenRef.TraitSelfPosition);
        Assert.Empty(rewrittenRef.TraitSelfParameterIndices);
        Assert.False(rewrittenRef.TraitSelfInResult);
    }
}
