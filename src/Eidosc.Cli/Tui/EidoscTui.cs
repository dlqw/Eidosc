using System.Diagnostics;
using Eidosc.Cli.Resources;
using Spectre.Console;

namespace Eidosc.Cli.Tui;

/// <summary>
/// Eidosc TUI 应用程序
/// </summary>
public sealed class EidoscTui
{
    private string _workspacePath;
    private readonly string _srcPath;
    private readonly string _debugPath;
    private readonly string _buildPath;
    private readonly string _logPath;

    public EidoscTui(string? workspacePath = null)
    {
        _workspacePath = workspacePath ?? Directory.GetCurrentDirectory();
        _srcPath = Path.Combine(_workspacePath, "src");
        _debugPath = Path.Combine(_workspacePath, "debug");
        _buildPath = Path.Combine(_workspacePath, "build");
        _logPath = Path.Combine(_workspacePath, "logs");
    }

    /// <summary>
    /// 运行 TUI 应用程序
    /// </summary>
    public async Task RunAsync()
    {
        AnsiConsole.Console.Clear();

        while (true)
        {
            AnsiConsole.Console.Clear();
            RenderHeader();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(CliMessages.TuiSelectActionTitle)
                    .PageSize(10)
                    .AddChoices([
                        CliMessages.TuiChooseWorkspaceChoice,
                        CliMessages.TuiBuildProjectChoice,
                        CliMessages.TuiDebugCompileChoice,
                        CliMessages.TuiAnalyzeProjectChoice,
                        CliMessages.TuiViewLogsChoice,
                        CliMessages.TuiConfigureChoice,
                        CliMessages.TuiExitChoice
                    ]));

            try
            {
                switch (choice)
                {
                    case var selected when selected == CliMessages.TuiChooseWorkspaceChoice:
                        SelectWorkspace();
                        break;
                    case var selected when selected == CliMessages.TuiBuildProjectChoice:
                        await BuildProjectAsync();
                        break;
                    case var selected when selected == CliMessages.TuiDebugCompileChoice:
                        await DebugCompileAsync();
                        break;
                    case var selected when selected == CliMessages.TuiAnalyzeProjectChoice:
                        await AnalyzeProjectAsync();
                        break;
                    case var selected when selected == CliMessages.TuiViewLogsChoice:
                        ViewLogs();
                        break;
                    case var selected when selected == CliMessages.TuiConfigureChoice:
                        Configure();
                        break;
                    case var selected when selected == CliMessages.TuiExitChoice:
                        AnsiConsole.MarkupLine(CliMessages.TuiGoodbye);
                        return;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(CliMessages.TuiError(ex.Message));
                AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
                Console.ReadKey();
            }
        }
    }

    private void RenderHeader()
    {
        var panel = new Panel(
            new Markup(CliMessages.TuiHeader(_workspacePath, _srcPath, _debugPath, _buildPath, _logPath)))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("cyan"),
            Padding = new Padding(1, 1)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private void SelectWorkspace()
    {
        AnsiConsole.MarkupLine(CliMessages.TuiCurrentWorkspace(_workspacePath));
        AnsiConsole.WriteLine();

        var options = new List<string>
        {
            CliMessages.TuiInputPathChoice,
            CliMessages.TuiBrowseDirectoryChoice,
            CliMessages.TuiReturnChoice
        };
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(CliMessages.TuiSelectWorkspaceMethodTitle)
                .AddChoices(options));

        switch (choice)
        {
            case var selected when selected == CliMessages.TuiInputPathChoice:
                var path = AnsiConsole.Ask<string>(CliMessages.TuiWorkspacePathPrompt);
                if (Directory.Exists(path))
                {
                    _workspacePath = Path.GetFullPath(path);
                    AnsiConsole.MarkupLine(CliMessages.TuiWorkspaceUpdated);
                }
                else
                {
                    AnsiConsole.MarkupLine(CliMessages.TuiDirectoryMissing);
                }
                break;

            case var selected when selected == CliMessages.TuiBrowseDirectoryChoice:
                BrowseDirectory();
                break;
        }

        AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
        Console.ReadKey();
    }

    private void BrowseDirectory()
    {
        var currentDir = _workspacePath;

        while (true)
        {
            AnsiConsole.Console.Clear();
            AnsiConsole.MarkupLine(CliMessages.TuiBrowseDirectoryHeader(currentDir));

            var directories = Directory.GetDirectories(currentDir)
                .Select(Path.GetFileName)
                .Where(d => d != null)
                .Cast<string>()
                .ToList();

            var options = new List<string>
            {
                CliMessages.TuiParentDirectoryChoice,
                CliMessages.TuiSelectCurrentDirectoryChoice,
                CliMessages.TuiCancelChoice
            };
            options.AddRange(directories.Select(CliMessages.TuiDirectoryChoice));

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(CliMessages.TuiSelectDirectoryTitle)
                    .PageSize(15)
                    .AddChoices(options));

            if (choice == CliMessages.TuiSelectCurrentDirectoryChoice)
            {
                _workspacePath = currentDir;
                return;
            }

            if (choice == CliMessages.TuiCancelChoice)
            {
                return;
            }

            if (choice == CliMessages.TuiParentDirectoryChoice)
            {
                var parent = Directory.GetParent(currentDir);
                if (parent != null)
                {
                    currentDir = parent.FullName;
                }
            }
            else
            {
                var dirName = directories.FirstOrDefault(directory =>
                    string.Equals(choice, CliMessages.TuiDirectoryChoice(directory), StringComparison.Ordinal));
                if (dirName == null)
                {
                    continue;
                }

                currentDir = Path.Combine(currentDir, dirName);
            }
        }
    }

    private async Task BuildProjectAsync()
    {
        var sourceFiles = GetSourceFiles();
        if (sourceFiles.Count == 0)
        {
            AnsiConsole.MarkupLine(CliMessages.TuiNoSourceFiles);
            AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
            Console.ReadKey();
            return;
        }

        var selectedFile = SelectSourceFile(sourceFiles, CliMessages.TuiSelectBuildFileTitle);
        if (selectedFile == null) return;

        var defaultExeName = Path.GetFileNameWithoutExtension(selectedFile) +
                             (OperatingSystem.IsWindows() ? ".exe" : "");
        var defaultOutputPath = Path.Combine(_buildPath, defaultExeName);

        var outputPath = AnsiConsole.Ask(CliMessages.TuiOutputFilePrompt, defaultOutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await AnsiConsole.Status()
            .StartAsync(CliMessages.TuiBuildInProgress, async ctx =>
            {
                ctx.Status(CliMessages.TuiCompilingStatus);

                var result = await RunCompilerAsync("build", selectedFile,
                    $"-t Native -o \"{outputPath}\"");

                ctx.Status(CliMessages.TuiCompleteStatus);
                DisplayCompilerResult(result);
            });

        AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
        Console.ReadKey();
    }

    private async Task DebugCompileAsync()
    {
        var sourceFiles = GetSourceFiles();
        if (sourceFiles.Count == 0)
        {
            AnsiConsole.MarkupLine(CliMessages.TuiNoSourceFiles);
            AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
            Console.ReadKey();
            return;
        }

        var selectedFile = SelectSourceFile(sourceFiles, CliMessages.TuiSelectDebugFileTitle);
        if (selectedFile == null) return;

        var debugOutput = AnsiConsole.Ask(CliMessages.TuiDebugOutputPrompt, _debugPath);
        Directory.CreateDirectory(debugOutput);

        var debugLevelChoices = CreateDebugLevelChoices();
        var debugLevelChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(CliMessages.TuiSelectDebugLevelTitle)
                .AddChoices(debugLevelChoices.Keys));
        var debugLevel = debugLevelChoices[debugLevelChoice];

        await AnsiConsole.Status()
            .StartAsync(CliMessages.TuiDebugInProgress, async ctx =>
            {
                ctx.Status(CliMessages.TuiCompilingStatus);

                var result = await RunCompilerAsync("debug", selectedFile,
                    $"--debug-output {debugOutput} --debug-level {debugLevel}");

                ctx.Status(CliMessages.TuiCompleteStatus);
                DisplayCompilerResult(result);
            });

        AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
        Console.ReadKey();
    }

    private async Task AnalyzeProjectAsync()
    {
        var sourceFiles = GetSourceFiles();
        if (sourceFiles.Count == 0)
        {
            AnsiConsole.MarkupLine(CliMessages.TuiNoSourceFiles);
            AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
            Console.ReadKey();
            return;
        }

        var selectedFile = SelectSourceFile(sourceFiles, CliMessages.TuiSelectAnalyzeFileTitle);
        if (selectedFile == null) return;

        var phaseChoices = CreateAnalyzePhaseChoices();
        var phaseChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(CliMessages.TuiSelectAnalyzePhaseTitle)
                .AddChoices(phaseChoices.Keys));
        var phase = phaseChoices[phaseChoice];

        await AnsiConsole.Status()
            .StartAsync(CliMessages.TuiAnalyzeInProgress, async ctx =>
            {
                ctx.Status(CliMessages.TuiAnalyzingStatus);

                var result = await RunCompilerAsync("analyze", selectedFile,
                    $"--phase {phase}");

                ctx.Status(CliMessages.TuiCompleteStatus);
                DisplayCompilerResult(result);
            });

        AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
        Console.ReadKey();
    }

    private void ViewLogs()
    {
        if (!Directory.Exists(_logPath))
        {
            AnsiConsole.MarkupLine(CliMessages.TuiLogDirectoryMissing);
            AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
            Console.ReadKey();
            return;
        }

        var logFiles = Directory.GetFiles(_logPath, "*.log")
            .OrderByDescending(f => File.GetCreationTime(f))
            .Take(20)
            .ToList();

        if (logFiles.Count == 0)
        {
            AnsiConsole.MarkupLine(CliMessages.TuiNoLogFiles);
            AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
            Console.ReadKey();
            return;
        }

        var options = new List<string> { CliMessages.TuiReturnChoice };
        options.AddRange(logFiles.Select(f =>
            CliMessages.TuiLogFileChoice(Path.GetFileName(f), File.GetCreationTime(f))));

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(CliMessages.TuiSelectLogFileTitle)
                .PageSize(15)
                .AddChoices(options));

        if (choice == CliMessages.TuiReturnChoice) return;

        var filePath = logFiles.FirstOrDefault(f => choice.StartsWith(Path.GetFileName(f)));
        if (filePath != null)
        {
            var content = File.ReadAllText(filePath);
            AnsiConsole.Console.Clear();
            AnsiConsole.MarkupLine(CliMessages.TuiLogContentHeader);
            AnsiConsole.WriteLine(new string('=', 50));

            // 使用分页显示
            var lines = content.Split('\n');
            var page = 0;
            const int pageSize = 30;

            while (true)
            {
                AnsiConsole.Console.Clear();
                AnsiConsole.MarkupLine(CliMessages.TuiLogPageHeader(Path.GetFileName(filePath), page + 1));
                AnsiConsole.WriteLine(new string('=', 50));

                foreach (var line in lines.Skip(page * pageSize).Take(pageSize))
                {
                    AnsiConsole.WriteLine(line);
                }

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(CliMessages.TuiLogPageNavigation);

                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Q) break;
                if (key.Key == ConsoleKey.N && (page + 1) * pageSize < lines.Length) page++;
                if (key.Key == ConsoleKey.P && page > 0) page--;
            }
        }
    }

    private static Dictionary<string, string> CreateDebugLevelChoices() => new(StringComparer.Ordinal)
    {
        [CliMessages.TuiDebugLevelMinimalChoice] = "Minimal",
        [CliMessages.TuiDebugLevelNormalChoice] = "Normal",
        [CliMessages.TuiDebugLevelVerboseChoice] = "Verbose",
        [CliMessages.TuiDebugLevelDiagnosticChoice] = "Diagnostic"
    };

    private static Dictionary<string, string> CreateAnalyzePhaseChoices() => new(StringComparer.Ordinal)
    {
        [CliMessages.TuiAnalyzePhaseAllChoice] = "all",
        [CliMessages.TuiAnalyzePhaseLexerChoice] = "lexer",
        [CliMessages.TuiAnalyzePhaseParserChoice] = "parser",
        [CliMessages.TuiAnalyzePhaseNamerChoice] = "namer",
        [CliMessages.TuiAnalyzePhaseTypesChoice] = "types",
        [CliMessages.TuiAnalyzePhaseAbilitiesChoice] = "abilities",
        [CliMessages.TuiAnalyzePhaseHirChoice] = "hir",
        [CliMessages.TuiAnalyzePhaseMirChoice] = "mir",
        [CliMessages.TuiAnalyzePhaseBorrowChoice] = "borrow",
        [CliMessages.TuiAnalyzePhaseLlvmChoice] = "llvm"
    };

    private void Configure()
    {
        while (true)
        {
            AnsiConsole.Console.Clear();
            AnsiConsole.MarkupLine(CliMessages.TuiConfigTitle);
            AnsiConsole.WriteLine(new string('=', 30));

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(CliMessages.TuiConfigOptionsTitle)
                    .AddChoices([
                        CliMessages.TuiWorkspacePathOption(_workspacePath),
                        CliMessages.TuiSourceDirectoryOption(_srcPath),
                        CliMessages.TuiDebugOutputDirectoryOption(_debugPath),
                        CliMessages.TuiBuildOutputDirectoryOption(_buildPath),
                        CliMessages.TuiLogDirectoryOption(_logPath),
                        CliMessages.TuiCreateDirectoryStructureChoice,
                        CliMessages.TuiReturnChoice
                    ]));

            if (choice == CliMessages.TuiReturnChoice) break;

            if (choice == CliMessages.TuiCreateDirectoryStructureChoice)
            {
                CreateDirectoryStructure();
                continue;
            }

            // 配置路径
            var newPath = AnsiConsole.Ask<string>(CliMessages.TuiNewPathPrompt(_workspacePath));
            if (Directory.Exists(newPath))
            {
                _workspacePath = Path.GetFullPath(newPath);
                AnsiConsole.MarkupLine(CliMessages.TuiUpdated);
            }
            else
            {
                AnsiConsole.MarkupLine(CliMessages.TuiDirectoryMissing);
            }

            AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
            Console.ReadKey();
        }
    }

    private void CreateDirectoryStructure()
    {
        try
        {
            Directory.CreateDirectory(_srcPath);
            Directory.CreateDirectory(_debugPath);
            Directory.CreateDirectory(_buildPath);
            Directory.CreateDirectory(_logPath);

            AnsiConsole.MarkupLine(CliMessages.TuiDirectoryStructureCreated);
            AnsiConsole.MarkupLine("  [blue]{0}[/]", _srcPath);
            AnsiConsole.MarkupLine("  [yellow]{0}[/]", _debugPath);
            AnsiConsole.MarkupLine("  [magenta]{0}[/]", _buildPath);
            AnsiConsole.MarkupLine("  [white]{0}[/]", _logPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(CliMessages.TuiCreateDirectoryFailed(ex.Message));
        }

        AnsiConsole.WriteLine(CliMessages.TuiPressAnyKey);
        Console.ReadKey();
    }

    private List<string> GetSourceFiles()
    {
        var files = new List<string>();

        if (Directory.Exists(_srcPath))
        {
            files.AddRange(Directory.GetFiles(_srcPath, "*.eidos", SearchOption.AllDirectories));
        }

        // 也检查工作空间根目录
        if (Directory.Exists(_workspacePath))
        {
            files.AddRange(Directory.GetFiles(_workspacePath, "*.eidos"));
        }

        return files.Distinct().ToList();
    }

    private string? SelectSourceFile(List<string> files, string title)
    {
        var options = new List<string> { CliMessages.TuiReturnChoice };
        options.AddRange(files.Select(f =>
        {
            var relativePath = Path.GetRelativePath(_workspacePath, f);
            return relativePath.Length < f.Length ? relativePath : Path.GetFileName(f);
        }));

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .PageSize(15)
                .AddChoices(options));

        if (choice == CliMessages.TuiReturnChoice) return null;

        var index = options.IndexOf(choice) - 1;
        return files[index];
    }

    private async Task<(bool Success, string Output, string Error)> RunCompilerAsync(
        string command, string sourceFile, string args)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project src/Eidosc/Eidosc.Cli -- {command} \"{sourceFile}\" {args}",
                WorkingDirectory = FindSolutionRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                return (false, "", CliMessages.TuiCompilerProcessStartFailed);
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            return (process.ExitCode == 0, output, error);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private string FindSolutionRoot()
    {
        var dir = _workspacePath;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Eidosc.sln")) ||
                Directory.Exists(Path.Combine(dir, "src", "Eidosc")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return _workspacePath;
    }

    private void DisplayCompilerResult((bool Success, string Output, string Error) result)
    {
        AnsiConsole.WriteLine();

        if (result.Success)
        {
            AnsiConsole.MarkupLine(CliMessages.TuiCompileSucceeded);
        }
        else
        {
            AnsiConsole.MarkupLine(CliMessages.TuiCompileFailed);
        }

        if (!string.IsNullOrEmpty(result.Output))
        {
            AnsiConsole.MarkupLine(CliMessages.TuiOutputHeader);
            AnsiConsole.WriteLine(result.Output);
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            AnsiConsole.MarkupLine(CliMessages.TuiErrorHeader);
            AnsiConsole.WriteLine(result.Error);
        }
    }
}
