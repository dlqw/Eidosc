using Xunit;

namespace Eidosc.Tests.Unit.Governance;

public sealed class TestSuiteGovernanceTests
{
    private const int MaxSourceFileLines = 1499;

    [Fact]
    public void TestSourceFiles_StayBelowGovernanceLineLimit()
    {
        var testProjectDir = FindTestProjectDir();
        var oversizedFiles = Directory
            .EnumerateFiles(testProjectDir, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !IsGeneratedOrBuildOutput(path))
            .Select(static path => new
            {
                Path = path,
                Lines = File.ReadLines(path).Count()
            })
            .Where(static file => file.Lines > MaxSourceFileLines)
            .OrderByDescending(static file => file.Lines)
            .ThenBy(static file => file.Path, StringComparer.Ordinal)
            .Select(file => $"{Path.GetRelativePath(testProjectDir, file.Path)} ({file.Lines})")
            .ToArray();

        Assert.True(
            oversizedFiles.Length == 0,
            "Eidosc.Tests C# source files must stay below 1500 lines:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, oversizedFiles));
    }

    private static string FindTestProjectDir()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, "Eidosc.Tests.csproj");
            if (File.Exists(candidate))
            {
                return dir;
            }

            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }

            dir = parent.FullName;
        }

        var sourceRelative = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        if (File.Exists(Path.Combine(sourceRelative, "Eidosc.Tests.csproj")))
        {
            return sourceRelative;
        }

        throw new DirectoryNotFoundException("Eidosc.Tests project directory was not found.");
    }

    private static bool IsGeneratedOrBuildOutput(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) ||
               normalized.Contains("/obj/", StringComparison.Ordinal) ||
               normalized.Contains("/TestResults/", StringComparison.Ordinal);
    }
}
