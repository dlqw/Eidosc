using System.Collections;
using System.Reflection;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class PreludeCoreImageVisibilityTests
{
    [Fact]
    public void CompilationPipeline_ImplicitPrelude_ProvidesGenericDisplayPrintOverloads()
    {
        const string source = """
main :: Unit -> Unit need io {
    print(42);
    print("hello");
    print(true);
    println(3.5);
    println()
}
""";

        var result = Compile(source);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_ImplicitPrelude_IsOpenedInsideNestedUserModules()
    {
        const string source = """
Output :: module {
    export emit :: Unit -> Unit need io {
        _ => println("nested")
    }
}

main :: Unit -> Unit need io { Output.emit() }
""";

        var result = Compile(source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var nestedPrintln = Assert.Single(
            AstStableNodeTraversal
                .Enumerate(Assert.IsType<ModuleDecl>(result.Ast))
                .Select(static entry => entry.Node)
                .OfType<IdentifierExpr>(),
            identifier => identifier.Name == "println" && identifier.Span.Position < source.Length);
        Assert.True(nestedPrintln.SymbolId.IsValid);
    }

    [Fact]
    public void CompilationPipeline_NoImplicitPrelude_RemovesBindingsFromNestedUserModules()
    {
        const string source = """
Output :: module {
    export emit :: Unit -> Unit need io {
        _ => println("nested")
    }
}

main :: Unit -> Unit need io { Output.emit() }
""";

        var result = Compile(source, noImplicitPrelude: true);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Code == "E3000" &&
            diagnostic.Message.Contains("println", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ImportedStdModule_PreludeCallsKeepStableSymbolsThroughMir()
    {
        const string source = """
import std.Console

main :: Unit -> Int need io {
    Console.write("plain");
    Console.write_line("prefix=")(9);
    0
}
""";

        var result = Compile(
            source,
            stopAtPhase: CompilationPhase.Mir,
            packageImportRoots: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [WellKnownStrings.Std.Module] = []
            });

        Assert.True(result.Success, FormatDiagnostics(result));

        var astCalls = AstStableNodeTraversal
            .Enumerate(Assert.IsType<ModuleDecl>(result.Ast))
            .Select(static entry => entry.Node)
            .OfType<IdentifierExpr>()
            .Where(static identifier =>
                identifier.Name is "print" or "println" &&
                identifier.Span.FilePath?.EndsWith("console.eidos", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        Assert.NotEmpty(astCalls);
        Assert.All(astCalls, static identifier => Assert.True(identifier.SymbolId.IsValid));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.All(astCalls, identifier =>
        {
            var owner = Assert.Single(symbolTable.Modules.GetModules(), module => module.Members.Contains(identifier.SymbolId));
            Assert.Equal(PreludeCoreImageRegistry.PackageAlias, owner.PackageAlias);
            Assert.Equal($"precompiled:{PreludeCoreImageRegistry.PackageAlias}", owner.PackageInstanceKey);
        });

        var hirCalls = EnumerateHirNodes(Assert.IsType<HirModule>(result.HirModule))
            .OfType<HirVar>()
            .Where(static variable =>
                variable.Name is "print" or "println" &&
                variable.Span.FilePath?.EndsWith("console.eidos", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        Assert.NotEmpty(hirCalls);
        Assert.All(hirCalls, static variable => Assert.True(variable.SymbolId.IsValid));
        Assert.Equal(
            astCalls.Select(static call => call.SymbolId).Distinct().OrderBy(static id => id.Value).ToArray(),
            hirCalls.Select(static call => call.SymbolId).Distinct().OrderBy(static id => id.Value).ToArray());

        var preludeCallSymbolIds = astCalls.Select(static call => call.SymbolId).ToHashSet();
        var consoleCalls = Assert.IsType<MirModule>(result.MirModule).Functions
            .SelectMany(static function => function.BasicBlocks)
            .SelectMany(static block => block.Instructions)
            .OfType<MirCall>()
            .Where(call => call.Function is MirFunctionRef function && preludeCallSymbolIds.Contains(function.SymbolId))
            .ToArray();
        Assert.NotEmpty(consoleCalls);
        Assert.All(consoleCalls, call =>
        {
            var function = Assert.IsType<MirFunctionRef>(call.Function);
            Assert.True(function.SymbolId.IsValid);
            var owner = Assert.Single(symbolTable.Modules.GetModules(), module => module.Members.Contains(function.SymbolId));
            Assert.Equal(PreludeCoreImageRegistry.PackageAlias, owner.PackageAlias);
            Assert.Equal($"precompiled:{PreludeCoreImageRegistry.PackageAlias}", owner.PackageInstanceKey);
        });
    }

    [Fact]
    public void CompilationPipeline_DirectCoreImageCompilation_UsesPreludeOwnerIdentityAtEveryLoweringStage()
    {
        Assert.True(
            PreludeCoreImageRegistry.TryGetSource(
                [PreludeCoreImageRegistry.PackageAlias, "Display"],
                out var source));
        Assert.True(
            PreludeCoreImageRegistry.TryGetSourceFilePath(
                [PreludeCoreImageRegistry.PackageAlias, "Display"],
                out var sourcePath));

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = sourcePath,
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        var expectedPackageInstance = $"precompiled:{PreludeCoreImageRegistry.PackageAlias}";
        var ast = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.Equal(PreludeCoreImageRegistry.PackageAlias, ast.PackageAlias);
        Assert.Equal(expectedPackageInstance, ast.PackageInstanceKey);

        var hir = Assert.IsType<HirModule>(result.HirModule);
        Assert.Equal(PreludeCoreImageRegistry.PackageAlias, hir.PackageAlias);
        Assert.Equal(expectedPackageInstance, hir.PackageInstanceKey);

        var mir = Assert.IsType<MirModule>(result.MirModule);
        Assert.Equal(PreludeCoreImageRegistry.PackageAlias, mir.PackageAlias);
        Assert.Equal(expectedPackageInstance, mir.PackageInstanceKey);
    }

    [Fact]
    public void CompilationPipeline_ImplicitPrelude_ProvidesEqRoleAndRewritesAdtEqualityAtMir()
    {
        const string source = """
Thing :: type { Thing :: type(Int) }

EqThing :: instance Eq {
    eq :: Thing -> Thing -> Bool { _ => _ => true }
}

main :: Unit -> Int {
    value: Thing := Thing(1);
    if value == value then { 0 } else { 1 }
}
""";

        var result = Compile(source, stopAtPhase: CompilationPhase.Mir);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var eqTraitId = symbolTable.LookupType("Eq");
        Assert.True(eqTraitId.HasValue);
        var thingSymbolId = symbolTable.LookupType("Thing");
        Assert.True(thingSymbolId.HasValue);
        var thingTypeId = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(thingSymbolId.Value)).TypeId;
        var eqImpl = Assert.IsType<ImplSymbol>(symbolTable.LookupImplForTrait(thingTypeId, eqTraitId.Value));
        var eqMethodId = Assert.Single(eqImpl.Methods);
        var main = Assert.Single(Assert.IsType<MirModule>(result.MirModule).Functions, function => function.Name == "main");
        Assert.Contains(
            main.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>(),
            call => call.Function is MirFunctionRef { SymbolId: var symbolId } && symbolId == eqMethodId);
    }

    [Fact]
    public void CompilationPipeline_RawRuntimeOutputBridge_IsNotUserVisible()
    {
        const string source = """
main :: Unit -> Unit need io {
    write_text_raw("hidden")
}
""";

        var result = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Code == "E3000" &&
            diagnostic.Message.Contains("write_text_raw", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_NoImplicitPrelude_RemovesPreludeBindings()
    {
        const string source = """
main :: Unit -> Unit need io {
    print(1)
}
""";

        var result = Compile(source, noImplicitPrelude: true);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Code == "E3000" &&
            diagnostic.Message.Contains("print", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_StdImport_RequiresExplicitPackageDependencyAlias()
    {
        const string source = """
import std.Math
main :: Unit -> Int { Math.abs(-1) }
""";

        var withoutDependency = Compile(source, noImplicitPrelude: true);
        var withDependency = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "explicit_std_dependency.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false,
            NoImplicitPrelude = true,
            AllowVirtualInputFile = true,
            PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [WellKnownStrings.Std.Module] = []
            }
        }).Run();

        Assert.False(withoutDependency.Success);
        Assert.Contains(withoutDependency.Diagnostics, diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Message.Contains("std.Math", StringComparison.Ordinal));
        Assert.True(withDependency.Success, FormatDiagnostics(withDependency));
    }

    [Fact]
    public void CompilationPipeline_ModuleMembers_ArePrivateUnlessExplicitlyExported()
    {
        const string source = """
Hidden :: module {
    secret :: Unit -> Int { _ => 1 }
    export visible :: Unit -> Int { _ => 2 }
}

main :: Unit -> Int {
    Hidden.visible() + Hidden.secret()
}
""";

        var result = Compile(source, noImplicitPrelude: true);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Code == "E3000" &&
            diagnostic.Message.Contains("Hidden.secret", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Message.Contains("Hidden.visible", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_UserSourceCannotForgeCompilerInternalModuleAccess()
    {
        const string source = """
Provider :: module {
    helper :: Unit -> Int
        compiler(internal)
    {
        _ => 1
    }
}

Peer :: module {
    export call :: Unit -> Int {
        _ => Provider.helper()
    }
}

main :: Unit -> Int { Peer.call() }
""";

        var result = Compile(source, noImplicitPrelude: true);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Code == "E3000" &&
            diagnostic.Message.Contains("clause 'compiler' is reserved for toolchain-owned source", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CoreImage_RegistersEveryTypedElaborationRoleExactlyOnce()
    {
        var result = Compile("main :: Unit -> Int { 0 }");

        Assert.True(result.Success, FormatDiagnostics(result));
        var functions = result.SymbolTable!.Symbols.Values.OfType<FuncSymbol>().ToArray();
        foreach (var role in Enum.GetValues<CompilerSemanticRole>().Where(role => role != CompilerSemanticRole.None))
        {
            Assert.Single(functions, function => function.CompilerSemanticRole == role);
        }
    }

    private static CompilationResult Compile(
        string source,
        bool noImplicitPrelude = false,
        CompilationPhase stopAtPhase = CompilationPhase.Types,
        IReadOnlyDictionary<string, string[]>? packageImportRoots = null) =>
        new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "prelude_core_visibility.eidos",
            StopAtPhase = stopAtPhase,
            UseColors = false,
            NoImplicitPrelude = noImplicitPrelude,
            AllowVirtualInputFile = true,
            PackageImportRoots = packageImportRoots?.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal) ?? new Dictionary<string, string[]>(StringComparer.Ordinal)
        }).Run();

    private static IEnumerable<HirNode> EnumerateHirNodes(HirNode root)
    {
        var pending = new Stack<HirNode>();
        var visited = new HashSet<HirNode>(ReferenceEqualityComparer.Instance);
        pending.Push(root);

        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node))
            {
                continue;
            }

            yield return node;
            foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                switch (property.GetValue(node))
                {
                    case HirNode child:
                        pending.Push(child);
                        break;
                    case IEnumerable children when property.PropertyType != typeof(string):
                        foreach (var item in children)
                        {
                            if (item is HirNode nested)
                            {
                                pending.Push(nested);
                            }
                        }
                        break;
                }
            }
        }
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}"));
}
