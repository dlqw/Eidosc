using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Eidosc.Bindgen;

namespace Eidosc.Cli.Commands.Pkg;

public static class PkgBindCommand
{
    public static Command Create()
    {
        var command = new Command("bind", "Generate standalone Eidos binding packages for C libraries.")
        {
            CreateInitCommand(),
            CreateGenerateCommand()
        };
        return command;
    }

    private static Command CreateInitCommand()
    {
        var command = new Command("init", "Create a binding package skeleton.")
        {
            new Argument<string>("out-dir", "Binding package output directory."),
            new Option<string>("--package", "Eidos package name.") { IsRequired = true },
            new Option<string>("--library", "Native library name.") { IsRequired = true },
            new Option<string[]>("--header", "C header to bind.") { IsRequired = true },
            new Option<string[]>("--include", "C include path."),
            new Option<string[]>("--native-source", "Native source file to compile with the binding package."),
            new Option<string[]>("--linker-flag", "Native linker flag.")
        };

        command.Handler = CommandHandler.Create<InitOptions>(ExecuteInit);
        return command;
    }

    private static Command CreateGenerateCommand()
    {
        var command = new Command("generate", "Generate binding package files from bindgen.toml.")
        {
            new Argument<string>("binding-package-dir", "Binding package directory."),
            new Option<bool>("--check", "Check whether generated files are up to date without writing."),
            new Option<bool>("--no-shim", "Do not generate C shim source files."),
            new Option<bool>("--no-color", "Disable colored output.")
        };

        command.Handler = CommandHandler.Create<GenerateOptions>(ExecuteGenerate);
        return command;
    }

    private sealed class InitOptions
    {
        public string OutDir { get; set; } = "";
        public string Package { get; set; } = "";
        public string Library { get; set; } = "";
        public string[] Header { get; set; } = [];
        public string[] Include { get; set; } = [];
        public string[] NativeSource { get; set; } = [];
        public string[] LinkerFlag { get; set; } = [];
    }

    private sealed class GenerateOptions
    {
        public string BindingPackageDir { get; set; } = "";
        public bool Check { get; set; }
        public bool NoShim { get; set; }
        public bool NoColor { get; set; }
    }

    private static int ExecuteInit(InitOptions options)
    {
        try
        {
            new BindingPackageGenerator().Initialize(
                options.OutDir,
                options.Package,
                options.Library,
                options.Header,
                options.Include,
                options.NativeSource,
                options.LinkerFlag);
            Console.WriteLine($"Created binding package skeleton: {Path.GetFullPath(options.OutDir)}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int ExecuteGenerate(GenerateOptions options)
    {
        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(
            options.BindingPackageDir,
            options.Check,
            options.NoShim));

        foreach (var diagnostic in result.Diagnostics)
            Console.Error.WriteLine(diagnostic);

        foreach (var path in result.WrittenFiles)
            Console.WriteLine($"Generated: {path}");

        if (options.Check && result.ChangedFiles.Count > 0)
        {
            foreach (var path in result.ChangedFiles)
                Console.Error.WriteLine($"Out of date: {path}");
        }

        return result.Success ? 0 : 1;
    }
}
