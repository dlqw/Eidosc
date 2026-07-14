using Eidosc.ProjectSystem;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Pipeline;

public class SemanticVersionTests
{
    [Fact]
    public void Parse_BasicVersion()
    {
        var v = SemanticVersion.Parse("1.2.3");
        Assert.Equal(1, v.Major);
        Assert.Equal(2, v.Minor);
        Assert.Equal(3, v.Patch);
        Assert.Null(v.PreRelease);
    }

    [Fact]
    public void Parse_TagPrefix_RejectsNonSemVerInput()
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("v1.2.3"));
    }

    [Fact]
    public void Parse_IncompleteCore_RejectsNonSemVerInput()
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("1.2"));
    }

    [Fact]
    public void Parse_PreRelease()
    {
        var v = SemanticVersion.Parse("1.2.3-alpha.1");
        Assert.Equal("alpha.1", v.PreRelease);
        Assert.True(v.IsPreRelease);
    }

    [Fact]
    public void Parse_BuildMetadata()
    {
        var v = SemanticVersion.Parse("1.2.3+build.123");
        Assert.Equal("build.123", v.BuildMetadata);
    }

    [Fact]
    public void Compare_ReleaseGreaterThanPreRelease()
    {
        var release = new SemanticVersion(1, 0, 0);
        var pre = new SemanticVersion(1, 0, 0, "alpha");
        Assert.True(release > pre);
    }

    [Fact]
    public void Compare_Ordering()
    {
        var a = new SemanticVersion(1, 0, 0);
        var b = new SemanticVersion(1, 0, 1);
        var c = new SemanticVersion(1, 1, 0);
        var d = new SemanticVersion(2, 0, 0);

        Assert.True(a < b);
        Assert.True(b < c);
        Assert.True(c < d);
    }

    [Fact]
    public void Compare_PreReleaseOrdering()
    {
        var a = SemanticVersion.Parse("1.0.0-alpha");
        var b = SemanticVersion.Parse("1.0.0-alpha.1");
        var c = SemanticVersion.Parse("1.0.0-beta");
        Assert.True(a < b);
        Assert.True(b < c);
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        var v = new SemanticVersion(1, 2, 3, "alpha.1", "build.123");
        var parsed = SemanticVersion.Parse(v.ToString());
        Assert.Equal(v, parsed);
    }

    [Fact]
    public void TryParse_InvalidReturns()
    {
        Assert.False(SemanticVersion.TryParse("", out _));
        Assert.False(SemanticVersion.TryParse("abc", out _));
    }

    [Theory]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-alpha.01")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3+build..1")]
    [InlineData("1.2.3+build+2")]
    [InlineData("1.2.3-ä")]
    public void Parse_InvalidSemVer20_RejectsInput(string input)
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse(input));
    }

    [Fact]
    public void Compare_BuildMetadata_DoesNotAffectPrecedenceButDoesAffectIdentity()
    {
        var first = SemanticVersion.Parse("1.2.3+first");
        var second = SemanticVersion.Parse("1.2.3+second");

        Assert.Equal(0, first.CompareTo(second));
        Assert.True(first.HasSamePrecedenceAs(second));
        Assert.NotEqual(first, second);
    }
}

public class VersionRangeTests
{
    [Fact]
    public void Exact_MatchesOnlyExact()
    {
        var range = VersionRange.Parse("1.2.3");
        Assert.True(range.Contains(new SemanticVersion(1, 2, 3)));
        Assert.False(range.Contains(new SemanticVersion(1, 2, 4)));
    }

    [Fact]
    public void Caret_CompatibleRange()
    {
        var range = VersionRange.Parse("^1.2.3");
        Assert.True(range.Contains(new SemanticVersion(1, 2, 3)));
        Assert.True(range.Contains(new SemanticVersion(1, 9, 9)));
        Assert.False(range.Contains(new SemanticVersion(2, 0, 0)));
        Assert.False(range.Contains(new SemanticVersion(1, 2, 2)));
    }

    [Fact]
    public void Caret_ZeroMajor()
    {
        var range = VersionRange.Parse("^0.2.3");
        Assert.True(range.Contains(new SemanticVersion(0, 2, 3)));
        Assert.True(range.Contains(new SemanticVersion(0, 2, 9)));
        Assert.False(range.Contains(new SemanticVersion(0, 3, 0)));
    }

    [Fact]
    public void Caret_ZeroMajorAndMinor_OnlyAllowsPatchLine()
    {
        var range = VersionRange.Parse("^0.0.3");
        Assert.True(range.Contains(new SemanticVersion(0, 0, 3)));
        Assert.False(range.Contains(new SemanticVersion(0, 0, 4)));
        Assert.False(range.Contains(new SemanticVersion(0, 1, 0)));
    }

    [Fact]
    public void Tilde_PatchCompatible()
    {
        var range = VersionRange.Parse("~1.2.3");
        Assert.True(range.Contains(new SemanticVersion(1, 2, 3)));
        Assert.True(range.Contains(new SemanticVersion(1, 2, 9)));
        Assert.False(range.Contains(new SemanticVersion(1, 3, 0)));
    }

    [Fact]
    public void MinInclusive()
    {
        var range = VersionRange.Parse(">=1.2.0");
        Assert.True(range.Contains(new SemanticVersion(1, 2, 0)));
        Assert.True(range.Contains(new SemanticVersion(2, 0, 0)));
        Assert.False(range.Contains(new SemanticVersion(1, 1, 9)));
    }

    [Fact]
    public void Any()
    {
        var range = VersionRange.Parse("*");
        Assert.True(range.Contains(new SemanticVersion(0, 0, 1)));
        Assert.True(range.Contains(new SemanticVersion(99, 99, 99)));
    }
}

public class ContentHashTests
{
    [Fact]
    public void ComputeForDirectory_ProducesConsistentHash()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "Test.eidos"), "x :: 1;");
        File.WriteAllText(Path.Combine(temp.Path, "eidos.toml"), "manifestSchema = 3\n\n[language]\nversion = \"0.6.0-alpha.1\"\n");

        var hash1 = ContentHash.ComputeForDirectory(temp.Path);
        var hash2 = ContentHash.ComputeForDirectory(temp.Path);
        Assert.Equal(hash1, hash2);
        Assert.StartsWith("sha256:", hash1);
    }

    [Fact]
    public void ComputeForDirectory_ChangesWithContent()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "Test.eidos"), "x :: 1;");
        var hash1 = ContentHash.ComputeForDirectory(temp.Path);

        File.WriteAllText(Path.Combine(temp.Path, "Test.eidos"), "x :: 2;");
        var hash2 = ContentHash.ComputeForDirectory(temp.Path);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeForDirectory_ChangesWithTomlManifestContent()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "Test.eidos"), "x :: 1;");
        var manifestPath = Path.Combine(temp.Path, "eidos.toml");

        File.WriteAllText(manifestPath, "manifestSchema = 3");
        var hash1 = ContentHash.ComputeForDirectory(temp.Path);

        File.WriteAllText(manifestPath, "manifestSchema = 4");
        var hash2 = ContentHash.ComputeForDirectory(temp.Path);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_ProducesSameForSameInput()
    {
        Assert.Equal(ContentHash.ComputeHash("hello"), ContentHash.ComputeHash("hello"));
        Assert.NotEqual(ContentHash.ComputeHash("hello"), ContentHash.ComputeHash("world"));
    }
}

public class EidosLockFileTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        using var temp = new TempDirectory();
        var lockFile = new EidosLockFile();
        lockFile.Packages["Test"] = new LockedPackage
        {
            Source = "path",
            Path = "../test",
            ContentHash = "sha256:abc"
        };

        var path = Path.Combine(temp.Path, "eidos.lock.json");
        lockFile.Save(path);

        Assert.True(File.Exists(path));
        var loaded = EidosLockFile.Load(path);
        Assert.Equal("path", loaded.Packages["Test"].Source);
        Assert.Equal("../test", loaded.Packages["Test"].Path);
    }

    [Fact]
    public void Validate_DetectsMissingDirectory()
    {
        using var temp = new TempDirectory();
        var lockFile = new EidosLockFile();
        lockFile.Packages["Test"] = new LockedPackage
        {
            Source = "path",
            Path = "../nonexistent",
            ContentHash = "sha256:abc"
        };

        Assert.False(lockFile.Validate(temp.Path));
    }
}

public class DependencySpecTests
{
    [Fact]
    public void SourceKind_PathDetected()
    {
        var spec = new DependencySpec { Path = "../mylib" };
        Assert.Equal(DependencySourceKind.Path, spec.SourceKind);
    }

    [Fact]
    public void SourceKind_GitDetected()
    {
        var spec = new DependencySpec { Git = "https://github.com/test/repo", Tag = "v1.0" };
        Assert.Equal(DependencySourceKind.Git, spec.SourceKind);
    }

    [Fact]
    public void DisplayName_Path()
    {
        var spec = new DependencySpec { Path = "../mylib" };
        Assert.Equal("path:../mylib", spec.DisplayName);
    }

    [Fact]
    public void DisplayName_GitWithTag()
    {
        var spec = new DependencySpec { Git = "https://github.com/test/repo", Tag = "v1.0" };
        Assert.Contains("v1.0", spec.DisplayName);
    }
}

file sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
