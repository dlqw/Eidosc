using Eidosc.Symbols;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class TraitImplResolutionTests
{
    [Fact]
    public void CompilationPipeline_NameFirstInstance_MapsOverloadedTraitMethodsBySignature()
    {
        const string source = """
Format :: trait
{
    format :: Self -> String;
    format :: Self -> Int -> String;
}

FormatInt :: instance Format
{
    format :: Int -> String
    {
        value => "value"
    }

    format :: Int -> Int -> String
    {
        value => width => "value"
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_instance_overloaded_trait_methods.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Format");
        var intId = symbolTable.LookupType("Int");
        Assert.True(traitId.HasValue);
        Assert.True(intId.HasValue);

        var intSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(intId.Value));
        var impl = symbolTable.LookupImplForTrait(intSymbol.TypeId, traitId.Value);

        Assert.NotNull(impl);
        Assert.Equal(2, impl!.Methods.Count);
        Assert.Equal(2, impl.TraitMethodImplementations.Count);
        Assert.Equal(2, impl.TraitMethodImplementations.Keys.Distinct().Count());
    }

    [Fact]
    public void CompilationPipeline_NameFirstInstance_MissingOverloadedTraitMethod_ReportsDiagnostic()
    {
        const string source = """
Format :: trait
{
    format :: Self -> String;
    format :: Self -> Int -> String;
}

FormatInt :: instance Format
{
    format :: Int -> String
    {
        value => "value"
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_instance_missing_overloaded_trait_method.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("must implement overloaded trait method", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TraitOverloadedMethodDuplicateSignature_ReportsDiagnostic()
    {
        const string source = """
Format :: trait
{
    format :: Self -> String;
    format :: Self -> String;
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_overloaded_method_duplicate_signature.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3001" &&
                          diagnostic.Message.Contains("Duplicate overload for function 'format'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("trait 'Format'", StringComparison.Ordinal));
    }
}
