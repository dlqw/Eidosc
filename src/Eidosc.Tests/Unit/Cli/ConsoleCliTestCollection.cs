using Xunit;

namespace Eidosc.Tests.Unit.Cli;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCliTestCollection
{
    public const string Name = "Console CLI tests";
}
