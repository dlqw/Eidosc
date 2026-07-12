using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.CodeFormatting;
using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands;

public static class FmtCommand
{
    public static Command Create()
    {
        var command = new Command("fmt", CliMessages.FmtCommandDescription)
        {
            new Argument<string>("source", () => "", CliMessages.FmtSourceArgumentDescription),
            new Option<bool>("--stdin", CliMessages.FmtStdinOptionDescription),
            new Option<bool>("--write", CliMessages.FmtWriteOptionDescription),
            new Option<bool>("--check", CliMessages.FmtCheckOptionDescription),
            new Option<int>("--indent-size", () => 4, CliMessages.FmtIndentSizeOptionDescription),
            new Option<int>("--max-line-length", () => 100, CliMessages.FmtMaxLineLengthOptionDescription),
            new Option<bool>("--no-final-newline", CliMessages.FmtNoFinalNewlineOptionDescription),
            new Option<bool>("--no-validate", CliMessages.FmtNoValidateOptionDescription)
        };

        command.Handler = CommandHandler.Create<FmtOptions>(Execute);
        return command;
    }

    private sealed class FmtOptions
    {
        public string Source { get; set; } = "";
        public bool Stdin { get; set; }
        public bool Write { get; set; }
        public bool Check { get; set; }
        public int IndentSize { get; set; } = 4;
        public int MaxLineLength { get; set; } = 100;
        public bool NoFinalNewline { get; set; }
        public bool NoValidate { get; set; }
    }

    private static async Task<int> Execute(FmtOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();

        if (options.Write && options.Stdin)
        {
            Console.Error.WriteLine(CliMessages.FmtWriteStdinInvalid);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("fmt", false, commandStopwatch.Elapsed, useColors: false, details: CliMessages.FmtInvalidOptionsDetail, output: Console.Error);
            return 2;
        }

        var inputFile = NormalizeInputFile(options.Source);
        string sourceText;

        if (options.Stdin)
        {
            sourceText = await Console.In.ReadToEndAsync();
            CliOutput.WriteAction(CliMessages.FormattingAction, "stdin", useColors: false, output: Console.Error);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.Source))
            {
                Console.Error.WriteLine(CliMessages.FmtSourceRequiredUnlessStdin);
                commandStopwatch.Stop();
                CliOutput.WriteFinished("fmt", false, commandStopwatch.Elapsed, useColors: false, details: CliMessages.FmtMissingSourceDetail, output: Console.Error);
                return 2;
            }

            if (!File.Exists(inputFile))
            {
                Console.Error.WriteLine(CliMessages.FmtSourceFileNotFound(inputFile));
                commandStopwatch.Stop();
                CliOutput.WriteFinished("fmt", false, commandStopwatch.Elapsed, useColors: false, details: CliMessages.SourceFileMissingDetail, output: Console.Error);
                return 1;
            }

            sourceText = await File.ReadAllTextAsync(inputFile);
            CliOutput.WriteAction(CliMessages.FormattingAction, inputFile, useColors: false, output: Console.Error);
        }

        var result = EidosFormatter.Format(sourceText, inputFile, new EidosFormatterOptions
        {
            IndentSize = options.IndentSize,
            MaxLineLength = options.MaxLineLength,
            FinalNewline = !options.NoFinalNewline,
            ValidateSyntax = !options.NoValidate,
            LanguageVersion = ResolveLanguageVersion(inputFile, options.Stdin)
        });

        if (!result.Success)
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                Console.Error.WriteLine(CliMessages.FmtDiagnosticLine(diagnostic.Code ?? "E4000", diagnostic.Message));
            }
            commandStopwatch.Stop();
            CliOutput.WriteFinished("fmt", false, commandStopwatch.Elapsed, useColors: false, details: CliMessages.FmtSyntaxValidationFailedDetail, output: Console.Error);
            return 1;
        }

        if (options.Check)
        {
            if (string.Equals(sourceText, result.FormattedText, StringComparison.Ordinal))
            {
                commandStopwatch.Stop();
                CliOutput.WriteFinished("fmt", true, commandStopwatch.Elapsed, useColors: false, details: CliMessages.FmtAlreadyFormattedDetail, output: Console.Error);
                return 0;
            }

            Console.Error.WriteLine(CliMessages.FmtFormattingChangesRequired(inputFile));
            commandStopwatch.Stop();
            CliOutput.WriteFinished("fmt", false, commandStopwatch.Elapsed, useColors: false, details: CliMessages.FmtChangesRequiredDetail, output: Console.Error);
            return 1;
        }

        if (options.Write)
        {
            await File.WriteAllTextAsync(inputFile, result.FormattedText);
            CliOutput.WriteArtifact(CliMessages.ArtifactKindFormattedSource, inputFile, useColors: false, output: Console.Error);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("fmt", true, commandStopwatch.Elapsed, useColors: false, details: CliMessages.FmtWrittenDetail, output: Console.Error);
            return 0;
        }

        await Console.Out.WriteAsync(result.FormattedText);
        commandStopwatch.Stop();
        CliOutput.WriteFinished("fmt", true, commandStopwatch.Elapsed, useColors: false, details: CliMessages.FmtStdoutDetail, output: Console.Error);
        return 0;
    }

    private static string NormalizeInputFile(string inputFile)
    {
        if (string.IsNullOrWhiteSpace(inputFile))
        {
            return "stdin.eidos";
        }

        try
        {
            return Path.GetFullPath(inputFile);
        }
        catch
        {
            return inputFile;
        }
    }

    private static string ResolveLanguageVersion(string inputFile, bool isStdin)
    {
        if (isStdin && string.Equals(inputFile, "stdin.eidos", StringComparison.Ordinal))
        {
            return EidosLanguageVersions.DefaultForExistingProjects;
        }

        return EidosProjectConfigurationLoader.TryLoadNearest(inputFile)?.Configuration.LanguageVersion ??
               EidosLanguageVersions.DefaultForExistingProjects;
    }
}
