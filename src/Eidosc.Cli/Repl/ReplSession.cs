using Eidosc.Diagnostic;
using Eidosc.Cli.Resources;
using Eidosc.Hir;
using Eidosc.Interpreter;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Repl;

public sealed class ReplSession
{
    private readonly InterpreterEnvironment _env = new();
    private readonly EidosInterpreter _interpreter = new();
    private readonly string[] _importRoots;
    private readonly ReplCommandRegistry _commands;

    public ReplSession(string[] importRoots)
    {
        _importRoots = importRoots;
        BuiltinFunctions.RegisterAll(_env);
        _commands = new ReplCommandRegistry(this);
    }

    public async Task RunAsync()
    {
        PrintWelcome();

        while (true)
        {
            var input = ReadInput();
            if (input == null)
            {
                Console.WriteLine();
                break;
            }

            if (string.IsNullOrWhiteSpace(input))
                continue;

            input = input.Trim();

            if (input.StartsWith(':'))
            {
                var exitRequested = await _commands.ExecuteAsync(input);
                if (exitRequested) break;
                continue;
            }

            try
            {
                EvaluateInput(input);
            }
            catch (InterpreterException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(CliMessages.ReplError(ex.Message));
                Console.ResetColor();
            }
            catch (ReturnException)
            {
            }
            catch (BreakException)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(CliMessages.ReplBreakOutsideLoopError);
                Console.ResetColor();
            }
        }
    }

    internal void EvaluateInput(string input)
    {
        var wrappedSource = WrapInput(input);
        var result = CompileToHir(wrappedSource);
        if (result == null)
            return;

        if (!result.Success)
        {
            PrintDiagnostics(result);
            return;
        }

        if (result.HirModule == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(CliMessages.ReplNoHirError);
            Console.ResetColor();
            return;
        }

        var evalResult = EvalHirModule(result.HirModule);
        if (evalResult != null)
        {
            var (val, name) = evalResult.Value;
            ReplOutputFormatter.PrintValue(val, name);
        }
    }

    internal void EvaluateFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.ReplFileNotFound(filePath));
            Console.ResetColor();
            return;
        }

        var source = File.ReadAllText(filePath);
        var result = CompileToHir(source);
        if (result == null)
            return;

        if (!result.Success)
        {
            PrintDiagnostics(result);
            return;
        }

        if (result.HirModule != null)
        {
            EvalHirModule(result.HirModule);
        }

        Console.WriteLine(CliMessages.ReplFileLoaded(Path.GetFullPath(filePath)));
    }

    internal void PrintEnvironment()
    {
        var bindings = _env.AllBindings.ToList();
        if (bindings.Count == 0)
        {
            Console.WriteLine(CliMessages.ReplEnvironmentEmpty);
            return;
        }

        foreach (var (name, value) in bindings.OrderBy(b => b.Key))
        {
            Console.WriteLine($"  {name} = {value.Display()}");
        }
    }

    internal void ClearEnvironment()
    {
        Console.WriteLine(CliMessages.ReplEnvironmentCleared);
    }

    internal void ShowType(string input)
    {
        var wrappedSource = WrapInput(input);
        var result = CompileToHir(wrappedSource);
        if (result == null) return;

        if (!result.Success)
        {
            PrintDiagnostics(result);
            return;
        }

        if (result.HirModule != null)
        {
            var lastDecl = result.HirModule.Declarations.LastOrDefault();
            if (lastDecl != null && lastDecl.HasType)
            {
                Console.WriteLine($"  : {lastDecl.TypeId}");
                return;
            }
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(CliMessages.ReplCouldNotDetermineType);
        Console.ResetColor();
    }

    private string WrapInput(string input)
    {
        return $"module Repl {{ {input} }}";
    }

    private CompilationResult? CompileToHir(string source)
    {
        try
        {
            var options = new CompilationOptions
            {
                InputFile = "<repl>",
                StopAtPhase = CompilationPhase.Hir,
                DebugLevel = Eidosc.Debug.DebugLevel.Minimal,
                UseColors = false,
                ImportSearchRoots = _importRoots
            };

            var pipeline = new CompilationPipeline(source, options);
            return pipeline.Run();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.ReplCompilationError(ex.Message));
            Console.ResetColor();
            return null;
        }
    }

    private (RuntimeValue Value, string? Name)? EvalHirModule(HirModule module)
    {
        RuntimeValue? lastValue = null;
        string? lastName = null;

        foreach (var decl in module.Declarations)
        {
            var result = EvalDecl(decl);
            if (result != null)
            {
                lastValue = result.Value.Value;
                lastName = result.Value.Name;
            }
        }

        return lastValue != null ? (lastValue, lastName) : null;
    }

    private (string Name, RuntimeValue Value)? EvalDecl(HirDecl decl)
    {
        switch (decl)
        {
            case HirVal val:
            {
                var value = _interpreter.Eval(val.Initializer, _env);
                BindPatternToEnv(val.Pattern, value);
                if (val.Pattern is HirVarPattern vp && !vp.IsWildcard)
                    return (vp.Name, value);
                return ("_", value);
            }
            case HirVarDecl varDecl:
            {
                var value = _interpreter.Eval(varDecl.Initializer, _env);
                BindPatternToEnv(varDecl.Pattern, value);
                if (varDecl.Pattern is HirVarPattern vp && !vp.IsWildcard)
                    return (vp.Name, value);
                return ("_", value);
            }
            case HirFunc func:
            {
                var paramNames = func.Parameters.Select(p => p.Name).ToList();
                var funcValue = func.Body != null
                    ? new FuncValue(paramNames, func.Body, _env)
                    : new FuncValue(paramNames, new HirLiteral { LiteralKind = LiteralKind.Unit }, _env);
                _env.Bind(func.Name, funcValue);
                return (func.Name, funcValue);
            }
            case HirAdt adt:
            {
                foreach (var ctor in adt.Constructors)
                {
                    var ctorFunc = new BuiltinFuncValue(ctor.Name, args =>
                    {
                        var fields = args.ToList();
                        return new CtorValue(ctor.Name, fields);
                    });
                    _env.Bind(ctor.Name, ctorFunc);
                }
                return null;
            }
            default:
                return null;
        }
    }

    private void BindPatternToEnv(HirPattern pattern, RuntimeValue value)
    {
        switch (pattern)
        {
            case HirVarPattern vp when !vp.IsWildcard:
                _env.Bind(vp.Name, value);
                break;
            case HirTuplePattern tp when value is TupleValue tuple:
                for (var i = 0; i < Math.Min(tp.Elements.Count, tuple.Elements.Count); i++)
                    BindPatternToEnv(tp.Elements[i], tuple.Elements[i]);
                break;
            case HirCtorPattern cp when value is CtorValue ctor:
                for (var i = 0; i < Math.Min(cp.Fields.Count, ctor.Fields.Count); i++)
                    BindPatternToEnv(cp.Fields[i].Pattern, ctor.Fields[i]);
                break;
            case HirListPattern lp when value is ListValue list:
                for (var i = 0; i < Math.Min(lp.Elements.Count, list.Elements.Count); i++)
                    BindPatternToEnv(lp.Elements[i], list.Elements[i]);
                break;
            case HirAsPattern ap:
                _env.Bind(ap.Name, value);
                BindPatternToEnv(ap.InnerPattern, value);
                break;
        }
    }

    private static void PrintDiagnostics(CompilationResult result)
    {
        foreach (var diag in result.Diagnostics)
        {
            var color = diag.Level switch
            {
                DiagnosticLevel.Error => ConsoleColor.Red,
                DiagnosticLevel.Warning => ConsoleColor.Yellow,
                _ => ConsoleColor.Gray
            };
            Console.ForegroundColor = color;
            Console.WriteLine(diag.ToString());
        }
        Console.ResetColor();
    }

    private static string? ReadInput()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("eidos> ");
        Console.ResetColor();

        var line = Console.ReadLine();
        if (line == null) return null;

        var input = new System.Text.StringBuilder(line);
        var balance = CountBracketBalance(line);

        while (balance > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("    .. ");
            Console.ResetColor();

            var continuation = Console.ReadLine();
            if (continuation == null) break;

            input.AppendLine(continuation);
            balance += CountBracketBalance(continuation);
        }

        return input.ToString();
    }

    private static int CountBracketBalance(string line)
    {
        var balance = 0;
        var inString = false;
        var inChar = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && (inString || inChar)) { i++; continue; }
            if (c == '"' && !inChar) inString = !inString;
            if (c == '\'' && !inString) inChar = !inChar;
            if (inString || inChar) continue;

            if (c == '{' || c == '(' || c == '[') balance++;
            if (c == '}' || c == ')' || c == ']') balance--;
        }

        return balance;
    }

    private static void PrintWelcome()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(CliMessages.ReplTitle);
        Console.ResetColor();
        Console.WriteLine(CliMessages.ReplWelcomeHelp);
        Console.WriteLine();
    }
}
