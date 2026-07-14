using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Tests.Fixtures;
using Eidosc.Utils;
using Xunit;
using EidosAttribute = Eidosc.Ast.Attribute;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class NameResolverBoundaryTests
{
    [Fact]
    public void AttributeBinder_BindsFfiLibraryQualifiedSymbol()
    {
        var func = CreateFunction(
            "easyInit",
            CreateAttribute(WellKnownStrings.Keywords.Ffi, "\"curl/curl_easy_init\""));

        var result = new AttributeBinder().BindDeclarationAttributes(func, func.Name);

        Assert.NotNull(result.Ffi);
        Assert.Equal("curl_easy_init", result.Ffi.SymbolName);
        Assert.Equal("curl", result.Ffi.LibraryName);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AttributeBinder_BindsOperatorMetadata()
    {
        var func = CreateFunction(
            "append",
            CreateAttribute("operator", "infixr", "5"));

        var result = new AttributeBinder().BindDeclarationAttributes(func, func.Name);

        var operatorInfo = Assert.Single(result.Operators);
        Assert.Equal(CustomOperatorFixity.InfixR, operatorInfo.Fixity);
        Assert.Equal(5, operatorInfo.Precedence);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NameLookupService_PrefersLocalValue()
    {
        var symbolTable = new SymbolTable();
        symbolTable.InitializeGlobalScope();
        SymbolId local;
        using (symbolTable.PushScopeGuard(ScopeKind.Function))
        {
            local = symbolTable.DeclareVariable("value", SourceSpan.Empty);

            var lookup = new NameLookupService(symbolTable, symbolTable.PathResolver);
            var result = lookup.Lookup(
                "value",
                LookupKind.Value,
                new LookupContext(SymbolId.None, ImportScope: null));

            Assert.True(result.IsSuccess);
            Assert.Equal(local, result.SymbolId);
            Assert.False(result.IsConstructor);
        }
    }

    [Fact]
    public void NameLookupService_CanResolveConstructorFallback()
    {
        var symbolTable = new SymbolTable();
        symbolTable.InitializeGlobalScope();
        var ctorId = symbolTable.RegisterSymbol(new CtorSymbol
        {
            Name = "Some",
            Span = SourceSpan.Empty
        });
        symbolTable.CurrentScope!.BindConstructor("Some", ctorId);

        var lookup = new NameLookupService(symbolTable, symbolTable.PathResolver);
        var result = lookup.Lookup(
            "Some",
            LookupKind.Value | LookupKind.Constructor,
            new LookupContext(SymbolId.None, ImportScope: null));

        Assert.True(result.IsSuccess);
        Assert.Equal(ctorId, result.SymbolId);
        Assert.True(result.IsConstructor);
    }

    [Fact]
    public void CompilationPipeline_ImportedModuleQualifiedType_PrefersModuleAliasOverGlobalModulePath()
    {
        const string source = """
import Std.Task
import Std.HashMap

keep_task[A] :: Task.Task[A] -> Task.Task[A] { task => task }
keep_map :: HashMap.HashMap[Int, Int] -> HashMap.HashMap[Int, Int] { map => map }
keep_package_task[A] :: Std.Task.Task[A] -> Std.Task.Task[A] { task => task }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath("projects/test/src/semantic/imported_module_qualified_type.eidos"),
            AllowVirtualInputFile = true,
            StopAtPhase = CompilationPhase.Types,
            UseColors = false,
            PackageImportRoots =
            {
                ["Std"] =
                [
                    TestSourceLoader.GetFullPath("Eidosc/src/Eidosc/Stdlib/Precompiled")
                ]
            }
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    private static FuncDef CreateFunction(string name, params EidosAttribute[] attributes)
    {
        var func = new FuncDef();
        func.SetName(name);
        func.SetAttributes(attributes.ToList());
        return func;
    }

    private static EidosAttribute CreateAttribute(string name, params string[] arguments)
    {
        var attribute = new EidosAttribute();
        attribute.SetName(name);
        foreach (var argument in arguments)
        {
            attribute.AddArgumentText(argument);
        }

        return attribute;
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}"));
    }
}
