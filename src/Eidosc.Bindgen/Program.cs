using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosc.Bindgen.Models;
using System.Reflection;

namespace Eidosc.Bindgen;

public static class Program
{
    public static int Main(string[] args)
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

        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: Eidosc.Bindgen <ir.json> [--library <name>] [--output <dir>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --library <name>  Library name for link directive (default: derived from header)");
            Console.Error.WriteLine("  --output <dir>    Output directory (default: current directory)");
            return 1;
        }

        var irPath = args[0];
        string? libraryName = null;
        string? outputDir = null;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--library" && i + 1 < args.Length)
                libraryName = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length)
                outputDir = args[++i];
        }

        if (!File.Exists(irPath))
        {
            Console.Error.WriteLine($"Error: IR file not found: {irPath}");
            return 1;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        string json = File.ReadAllText(irPath);
        var ir = JsonSerializer.Deserialize<CHeaderIr>(json, options);

        if (ir == null)
        {
            Console.Error.WriteLine("Error: Failed to deserialize IR JSON");
            return 1;
        }

        // Derive library name from header filename if not specified
        libraryName ??= Path.GetFileNameWithoutExtension(ir.Header) ?? "unknown";

        outputDir ??= Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDir);

        // Generate Eidos bindings
        var eidosGen = new EidosBindingGenerator(ir, libraryName);
        string eidosCode = eidosGen.Generate();
        string eidosPath = Path.Combine(outputDir, $"{libraryName}_ffi.eidos");
        File.WriteAllText(eidosPath, eidosCode);
        Console.WriteLine($"Generated: {eidosPath}");

        // Generate C shim if needed
        var shimGen = new CShimGenerator(ir, libraryName);
        if (shimGen.HasShims())
        {
            string shimCode = shimGen.Generate();
            string shimPath = Path.Combine(outputDir, $"{libraryName}_shim.c");
            File.WriteAllText(shimPath, shimCode);
            Console.WriteLine($"Generated: {shimPath}");
        }

        // Print summary
        Console.WriteLine();
        Console.WriteLine($"Functions: {ir.Functions.Count}");
        Console.WriteLine($"Structs:   {ir.Structs.Count}");
        Console.WriteLine($"Enums:     {ir.Enums.Count}");
        Console.WriteLine($"Typedefs:  {ir.Typedefs.Count}");

        return 0;
    }
}
