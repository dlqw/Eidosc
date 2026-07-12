using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Fixtures;

public sealed class TestSourceLoaderTests
{
    [Fact]
    public void Load_ReusesCachedSourceText_WhenFixtureIsUnchanged()
    {
        var fixturePath = TestPathConfig.Current.Fixture("basic/literals.eidos");

        var first = TestSourceLoader.Load(fixturePath);
        var beforeSecondLoad = TestSourceLoader.GetCacheSnapshot();
        var second = TestSourceLoader.Load(fixturePath);
        var afterSecondLoad = TestSourceLoader.GetCacheSnapshot();

        Assert.Equal(first, second);
        Assert.True(
            afterSecondLoad.Hits > beforeSecondLoad.Hits,
            $"Expected source text cache hit count to increase. Before={beforeSecondLoad}, After={afterSecondLoad}");
    }
}
