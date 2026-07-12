using Eidosc.Interpreter;

namespace Eidosc.Cli.Repl;

public static class ReplOutputFormatter
{
    public static void PrintValue(RuntimeValue value, string? name)
    {
        switch (value)
        {
            case UnitValue:
                if (name != null)
                    Console.WriteLine($"  {name} = ()");
                break;
            default:
                if (name != null)
                    Console.WriteLine($"  {name} = {value.Display()}");
                else
                    Console.WriteLine($"  {value.Display()}");
                break;
        }
    }
}
