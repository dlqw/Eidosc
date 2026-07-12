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
    public void Run_TraitInvokeShowHelper_UsesMirTraitImplsForExplicitBuiltinImplWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var traitId = new SymbolId(1891);
        var traitMethodSymbol = new SymbolId(1892);
        var implMethodSymbol = new SymbolId(1893);
        var helperSymbol = new SymbolId(1894);
        var implId = new SymbolId(1895);

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("custom-int")
            },
            name: "show_int",
            symbolId: implMethodSymbol);

        var value = LocalPlace(1, intType);
        var result = LocalPlace(2, stringType);
        var helper = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = value.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "show",
                        SymbolId = traitMethodSymbol,
                        TypeId = stringType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0],
                        TraitMethodRole = TraitMethodRole.Show
                    },
                    Arguments = [value]
                }
            ],
            returnValue: result,
            name: "Std__TraitInvoke__show_value__spec_int",
            symbolId: helperSymbol,
            traitInvokeHelper: TraitInvokeHelperKind.ShowValue,
            traitInvokeHelperTraitId: traitId);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "show_helper_uses_mir_impl_metadata",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Show_Int",
                    Trait = traitId,
                    ImplementingType = intType,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, intType, "Int", []),
                    ImplementingTypeShape = new ImplConstructorShapeNode("Int", []) { TypeId = intType },
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, helper]
        });

        var rewrittenHelper = specialized.Functions.Single(function => function.SymbolId == helperSymbol);
        var rewrittenCall = Assert.Single(rewrittenHelper.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("show_int", rewrittenRef.Name);
    }

    [Fact]
    public void Run_UnresolvedTraitMethod_DoesNotRewriteToTraitInvokeHelperByName()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var traitMethodSymbol = new SymbolId(1851);
        var helperSymbol = new SymbolId(1852);
        var callerSymbol = new SymbolId(1853);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "show",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [],
            ReturnType = stringType
        });
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = helperSymbol,
            Name = "show_value",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [],
            ReturnType = stringType
        });

        var helper = BuildFunction(
            returnType: stringType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("helper")
            },
            name: "show_value",
            symbolId: helperSymbol);

        var callerResult = LocalPlace(1, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "show",
                        SymbolId = traitMethodSymbol,
                        TypeId = stringType
                    },
                    Arguments = []
                }
            ],
            returnValue: callerResult,
            name: "caller_unresolved_show",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "unresolved_trait_method_no_helper_name_bridge",
            Functions = [helper, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(traitMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("show", rewrittenRef.Name);
    }

    [Fact]
    public void Run_NameOnlyCallWithoutTraitIdentity_DoesNotDispatchToUniqueTraitMethod()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var traitMethodSymbol = new SymbolId(19250);
        var implMethodSymbol = new SymbolId(19251);
        var callerSymbol = new SymbolId(19252);

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
            ParamTypes = [intType],
            ReturnType = stringType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0]
        });

        var implId = symbolTable.DeclareImpl(traitId, intType, SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "display",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
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
                        SymbolId = SymbolId.None,
                        TraitOwnerId = SymbolId.None,
                        TypeId = stringType
                    },
                    Arguments = [argument]
                }
            ],
            returnValue: result,
            name: "caller_name_only_display",
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
                Value = new MirConstantValue.StringValue("int")
            },
            name: "display",
            symbolId: implMethodSymbol);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "name_only_trait_dispatch_is_not_guessed",
            Functions = [caller, implMethod]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(SymbolId.None, rewrittenRef.SymbolId);
        Assert.Equal("display", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitInvokeHelperWithTraitId_RewritesUnresolvedMethodByStructuredTrait()
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var implMethodSymbol = new SymbolId(1862);
        var helperSymbol = new SymbolId(1863);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Eq", SourceSpan.Empty);
        var widgetSymbolId = symbolTable.DeclareAdt("Widget", SourceSpan.Empty);
        var widgetSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(widgetSymbolId));
        var widgetType = widgetSymbol.TypeId;
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = new SymbolId(1861),
            Name = "eq",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [widgetType, widgetType],
            ReturnType = boolType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0, 1]
        });

        var implId = symbolTable.DeclareImpl(traitId, widgetType, SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "eq",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [widgetType, widgetType],
            ReturnType = boolType
        });
        symbolTable.AddMethodToImpl(implId, implMethodSymbol);

        var left = LocalPlace(1, widgetType);
        var right = LocalPlace(2, widgetType);
        var result = LocalPlace(3, boolType);
        var helper = BuildFunction(
            returnType: boolType,
            locals:
            [
                new MirLocal { Id = left.Local, Name = "left", TypeId = widgetType, IsParameter = true },
                new MirLocal { Id = right.Local, Name = "right", TypeId = widgetType, IsParameter = true },
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
                        SymbolId = SymbolId.None,
                        TypeId = boolType
                    },
                    Arguments = [left, right]
                }
            ],
            returnValue: result,
            name: "Std__TraitInvoke__eq_value__spec_string",
            symbolId: helperSymbol,
            traitInvokeHelper: TraitInvokeHelperKind.EqValue,
            traitInvokeHelperTraitId: traitId);

        var implMethod = BuildFunction(
            returnType: boolType,
            locals:
            [
                new MirLocal { Id = left.Local, Name = "left", TypeId = widgetType, IsParameter = true },
                new MirLocal { Id = right.Local, Name = "right", TypeId = widgetType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = boolType,
                Value = new MirConstantValue.BoolValue(true)
            },
            name: "Std__Text__eq",
            symbolId: implMethodSymbol,
            sourceName: "eq");

        var module = new MirModule
        {
            Name = "traitinvoke_helper_structured_trait_resolution",
            TraitImpls = MirTraitImplsFrom(symbolTable),
            Functions = [helper, implMethod]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenHelper = specialized.Functions.Single(function => function.SymbolId == helperSymbol);
        var rewrittenCall = Assert.Single(rewrittenHelper.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("Std__Text__eq", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitMethodWithSymbolId_DoesNotInferDispatchFromSameNameImplSignature()
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var directionType = new TypeId(1901);
        var posType = new TypeId(1902);
        var traitMethodSymbol = new SymbolId(1903);
        var directionImplMethodSymbol = new SymbolId(1904);
        var posImplMethodSymbol = new SymbolId(1905);
        var callerSymbol = new SymbolId(1906);

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
            ParamTypes = [TypeId.None, TypeId.None],
            ReturnType = TypeId.None,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0, 1],
            TraitMethodRole = TraitMethodRole.Equality
        });

        var directionImpl = symbolTable.DeclareImpl(traitId, directionType, SourceSpan.Empty);
        var posImpl = symbolTable.DeclareImpl(traitId, posType, SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = directionImplMethodSymbol,
            Name = "eq",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            ParamTypes = [directionType, directionType],
            ReturnType = boolType
        });
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = posImplMethodSymbol,
            Name = "eq",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            ParamTypes = [posType, posType],
            ReturnType = boolType
        });
        symbolTable.AddMethodToImpl(directionImpl, directionImplMethodSymbol, traitMethodSymbol);
        symbolTable.AddMethodToImpl(posImpl, posImplMethodSymbol, traitMethodSymbol);

        var left = LocalPlace(1, directionType);
        var right = LocalPlace(2, directionType);
        var result = LocalPlace(3, boolType);
        var caller = BuildFunction(
            returnType: boolType,
            locals:
            [
                new MirLocal { Id = left.Local, Name = "left", TypeId = directionType, IsParameter = true },
                new MirLocal { Id = right.Local, Name = "right", TypeId = directionType, IsParameter = true },
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
                        TypeId = TypeId.None,
                        SignatureTypeId = TypeId.None,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0, 1],
                        TraitMethodRole = TraitMethodRole.Equality
                    },
                    Arguments = [left, right]
                }
            ],
            returnValue: result,
            name: "caller_imported_eq",
            symbolId: callerSymbol);

        var directionEq = BuildFunction(
            returnType: boolType,
            locals:
            [
                new MirLocal { Id = left.Local, Name = "left", TypeId = directionType, IsParameter = true },
                new MirLocal { Id = right.Local, Name = "right", TypeId = directionType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirConstant { TypeId = boolType, Value = new MirConstantValue.BoolValue(true) },
            name: "direction_eq",
            symbolId: directionImplMethodSymbol);
        var posEq = BuildFunction(
            returnType: boolType,
            locals:
            [
                new MirLocal { Id = left.Local, Name = "left", TypeId = posType, IsParameter = true },
                new MirLocal { Id = right.Local, Name = "right", TypeId = posType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirConstant { TypeId = boolType, Value = new MirConstantValue.BoolValue(true) },
            name: "eq",
            symbolId: posImplMethodSymbol);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "trait_method_symbol_avoids_same_name_signature",
            TraitImpls = MirTraitImplsFrom(symbolTable),
            Functions = [caller, directionEq, posEq]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(directionImplMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_BuiltinEqLowering_RequiresStructuredEqualityRole()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var traitMethodSymbol = new SymbolId(1871);
        var callerSymbol = new SymbolId(1872);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("LooksLikeEq", SourceSpan.Empty);
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
            TraitMethodRole = TraitMethodRole.None
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
                        TypeId = boolType
                    },
                    Arguments = [left, right]
                }
            ],
            returnValue: result,
            name: "caller_looks_like_eq",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "builtin_eq_requires_structured_role",
            Functions = [caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        Assert.IsType<MirCall>(Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions));
    }

    [Fact]
    public void Run_BuiltinEqLowering_IgnoresStaleSymbolTableEqualityRole()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var traitMethodSymbol = new SymbolId(1875);
        var callerSymbol = new SymbolId(1876);

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
            TraitMethodRole = TraitMethodRole.Equality
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
                        TypeId = boolType
                    },
                    Arguments = [left, right]
                }
            ],
            returnValue: result,
            name: "caller_stale_symbol_table_eq_role",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "builtin_eq_ignores_stale_symbol_table_role",
            Functions = [caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        Assert.IsType<MirCall>(Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions));
    }

}
