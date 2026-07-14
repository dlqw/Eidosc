using System;
using System.Linq;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class IdeSemanticSnapshotTests
{
    [Fact]
    public void Build_SameScopeOverloads_ExposesGroupAndSelectedCallTarget()
    {
        const string source = """
format :: Int -> String
{
    _ => "int"
}

format :: String -> String
{
    text => text
}

main :: Unit -> String
{
    _ => format(1)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_same_scope_overloads.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var group = Assert.Single(snapshot.OverloadGroups, entry => entry.Name == "format");
        Assert.Equal(2, group.MemberSymbolIds.Count);
        Assert.Contains(group.Members, member => member.TypeText?.Contains("Int -> String", StringComparison.Ordinal) == true);
        Assert.Contains(group.Members, member => member.TypeText?.Contains("String -> String", StringComparison.Ordinal) == true);

        var intOverload = snapshot.Symbols.Single(symbol =>
            symbol.Name == "format" &&
            symbol.TypeText?.Contains("Int -> String", StringComparison.Ordinal) == true);
        var stringOverload = snapshot.Symbols.Single(symbol =>
            symbol.Name == "format" &&
            symbol.TypeText?.Contains("String -> String", StringComparison.Ordinal) == true);

        Assert.Contains(intOverload.SymbolId, group.MemberSymbolIds);
        Assert.Contains(stringOverload.SymbolId, group.MemberSymbolIds);

        var callStart = source.LastIndexOf("format(1)", StringComparison.Ordinal);
        Assert.Contains(
            snapshot.Occurrences,
            occurrence => occurrence.SymbolId == intOverload.SymbolId &&
                          occurrence.Role == "reference" &&
                          occurrence.Span.Start == callStart);

        var completions = snapshot.Completions
            .Where(completion => completion.Label == "format")
            .ToArray();
        Assert.Equal(2, completions.Length);
        Assert.All(completions, completion =>
        {
            Assert.Equal(group.GroupId, completion.OverloadGroupId);
            Assert.Equal(2, completion.OverloadCount);
            Assert.Contains(intOverload.SymbolId, completion.OverloadMemberSymbolIds);
            Assert.Contains(stringOverload.SymbolId, completion.OverloadMemberSymbolIds);
        });
    }

    [Fact]
    public void Build_AmbiguousCallableOverload_ExposesCallableCandidatesAndTypeContext()
    {
        const string source = """
A :: module {
    pick :: Int -> Int
    {
        value => value + 1
    }
}

B :: module {
    pick :: Int -> Int
    {
        value => value + 2
    }
}

import A.*
import B.*

main :: Unit -> Int
{
    _ => 1.pick()
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_ambiguous_callable_overload.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var diagnostic = Assert.Single(
            snapshot.Diagnostics,
            entry => entry.Code == "E4000" &&
                     entry.Message.Contains("Ambiguous callable overload 'pick'", StringComparison.Ordinal));

        Assert.Equal("callable-ambiguity", diagnostic.Metadata["resolution.kind"]);
        Assert.Equal("pick", diagnostic.Metadata["callable.name"]);
        Assert.Equal("method", diagnostic.Metadata["callable.syntax"]);
        Assert.Contains("Int", diagnostic.Metadata["callable.argumentTypes"], StringComparison.Ordinal);
        Assert.Contains("A.pick", diagnostic.Metadata["callable.candidates"], StringComparison.Ordinal);
        Assert.Contains("B.pick", diagnostic.Metadata["callable.candidates"], StringComparison.Ordinal);
        Assert.Contains("module=A", diagnostic.Metadata["callable.candidateDetails"], StringComparison.Ordinal);
        Assert.Contains("module=B", diagnostic.Metadata["callable.candidateDetails"], StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("argument types: Int", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("candidate: A.pick :: Int -> Int", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("candidate: B.pick :: Int -> Int", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("qualified paths: A.pick, B.pick", StringComparison.Ordinal));

        var qualifySuggestions = diagnostic.Suggestions
            .Where(suggestion => suggestion.Kind == "QualifySymbol")
            .ToArray();
        Assert.Equal(2, qualifySuggestions.Length);
        Assert.Contains(
            qualifySuggestions,
            suggestion => suggestion.Message.Contains("A.pick", StringComparison.Ordinal) &&
                          suggestion.OriginalSymbolId.HasValue);
        Assert.Contains(
            qualifySuggestions,
            suggestion => suggestion.Message.Contains("B.pick", StringComparison.Ordinal) &&
                          suggestion.OriginalSymbolId.HasValue);

        var typeSuggestion = Assert.Single(
            diagnostic.Suggestions,
            suggestion => suggestion.Kind == "ChangeType");
        Assert.Contains("type annotation", typeSuggestion.Message, StringComparison.Ordinal);
        Assert.Equal("medium", typeSuggestion.Confidence);
    }
}
