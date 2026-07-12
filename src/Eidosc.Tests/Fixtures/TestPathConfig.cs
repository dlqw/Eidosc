namespace Eidosc.Tests.Fixtures;

public sealed class TestPathConfig
{
    public static TestPathConfig Current { get; } = CreateFromEnvironment();

    public string FixtureProjectRoot { get; }

    public string FixtureSourceRoot { get; }

    public string EccProjectRoot { get; }

    public string TutorialExamplesRoot { get; }

    public string[] FixtureSourceRootSegments { get; }

    private TestPathConfig(
        string fixtureProjectRoot,
        string fixtureSourceRoot,
        string eccProjectRoot,
        string tutorialExamplesRoot)
    {
        FixtureProjectRoot = fixtureProjectRoot;
        FixtureSourceRoot = fixtureSourceRoot;
        EccProjectRoot = eccProjectRoot;
        TutorialExamplesRoot = tutorialExamplesRoot;
        FixtureSourceRootSegments = fixtureSourceRoot.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    public string Fixture(string relativePathUnderSourceRoot)
    {
        return CombineRelative(FixtureSourceRoot, relativePathUnderSourceRoot);
    }

    public string Ecc(string relativePathUnderEccRoot)
    {
        return CombineRelative(EccProjectRoot, relativePathUnderEccRoot);
    }

    public string TutorialExample(string relativePathUnderTutorialRoot)
    {
        return CombineRelative(TutorialExamplesRoot, relativePathUnderTutorialRoot);
    }

    private static TestPathConfig CreateFromEnvironment()
    {
        var fixtureProjectRoot = ReadEnvOrDefault("EIDOS_TEST_PROJECT_ROOT", "projects/test");
        var fixtureSourceRoot = CombineRelative(fixtureProjectRoot, "src");
        var eccProjectRoot = ReadEnvOrDefault("EIDOS_ECC_PROJECT_ROOT", "projects/ecc");
        var tutorialExamplesRoot = ReadEnvOrDefault("EIDOS_TUTORIAL_EXAMPLES_ROOT", "docs/tutorial/examples");
        return new TestPathConfig(fixtureProjectRoot, fixtureSourceRoot, eccProjectRoot, tutorialExamplesRoot);
    }

    private static string ReadEnvOrDefault(string key, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return NormalizeRelative(defaultValue);
        }

        return NormalizeRelative(value);
    }

    private static string CombineRelative(string left, string right)
    {
        var normalizedLeft = NormalizeRelative(left);
        var normalizedRight = NormalizeRelative(right);
        if (string.IsNullOrEmpty(normalizedLeft))
        {
            return normalizedRight;
        }

        if (string.IsNullOrEmpty(normalizedRight))
        {
            return normalizedLeft;
        }

        return $"{normalizedLeft}/{normalizedRight}";
    }

    private static string NormalizeRelative(string value)
    {
        return value.Replace('\\', '/').Trim('/');
    }
}
