using System.CommandLine;
using Eidosup.Commands;
using System.Reflection;

namespace Eidosup;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
        {
            var assembly = typeof(Program).Assembly;
            Console.WriteLine(
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown");
            return 0;
        }

        var root = new RootCommand("Bootstrap and maintain an Eidos development environment.")
        {
            SetupCommand.Create(),
            DoctorCommand.Create()
        };

        return await root.InvokeAsync(args);
    }
}
