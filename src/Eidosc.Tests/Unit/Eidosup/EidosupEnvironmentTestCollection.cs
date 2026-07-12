using Xunit;

namespace Eidosc.Tests.Unit.Eidosup;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EidosupEnvironmentTestCollection
{
    public const string Name = "Eidosup environment tests";
}
