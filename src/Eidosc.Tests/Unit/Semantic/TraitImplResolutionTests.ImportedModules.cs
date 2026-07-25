using Eidosc.Pipeline;
using Eidosc.Symbols;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class TraitImplResolutionTests
{
    [Fact]
    public void CompilationPipeline_Instance_QualifiedTraitPath_FromImportedModuleFile_Registers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_trait_impl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var moduleFile = Path.Combine(tempDir, "M.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        const string moduleSource = """
M :: module {
    export Show :: trait {
        show :: Self -> String
    }
}
""";

        const string entrySource = """
import M

Person :: type {
    Person:: type(String)
}

ShowPerson :: instance M.Show {
    show :: Person -> String {
        _ => "ok"
    }
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                UseColors = false,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [WellKnownStrings.Std.Module] = []
                }
            }).Run();

            Assert.True(result.Success);

            var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
            var traitResolution = symbolTable.ResolvePathWithResult(["M", "Show"]);
            Assert.True(traitResolution.IsSuccess);

            var personId = symbolTable.LookupType("Person");
            Assert.True(personId.HasValue);

            var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
            var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitResolution.SymbolId);

            Assert.NotNull(impl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
