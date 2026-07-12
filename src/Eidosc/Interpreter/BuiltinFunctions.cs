namespace Eidosc.Interpreter;

public static class BuiltinFunctions
{
    public static void RegisterAll(InterpreterEnvironment env)
    {
        // Arithmetic
        env.Bind("abs", Builtin("abs", args =>
        {
            if (args[0] is IntValue i) return new IntValue(Math.Abs(i.Value));
            if (args[0] is FloatValue f) return new FloatValue(Math.Abs(f.Value));
            throw new InterpreterException(InterpreterMessages.AbsExpectedIntOrFloat);
        }));

        env.Bind("min", Builtin("min", args =>
        {
            return (args[0], args[1]) switch
            {
                (IntValue a, IntValue b) => new IntValue(Math.Min(a.Value, b.Value)),
                (FloatValue a, FloatValue b) => new FloatValue(Math.Min(a.Value, b.Value)),
                _ => throw new InterpreterException(InterpreterMessages.MinExpectedMatchingNumericTypes)
            };
        }));

        env.Bind("max", Builtin("max", args =>
        {
            return (args[0], args[1]) switch
            {
                (IntValue a, IntValue b) => new IntValue(Math.Max(a.Value, b.Value)),
                (FloatValue a, FloatValue b) => new FloatValue(Math.Max(a.Value, b.Value)),
                _ => throw new InterpreterException(InterpreterMessages.MaxExpectedMatchingNumericTypes)
            };
        }));

        // String
        env.Bind("toString", Builtin("toString", args => new StringValue(args[0].Display())));
        env.Bind("stringLength", Builtin("stringLength", args => new IntValue(args[0].AssertType<StringValue>().Value.Length)));
        env.Bind("stringConcat", Builtin("stringConcat", args =>
            new StringValue(args[0].AssertType<StringValue>().Value + args[1].AssertType<StringValue>().Value)));

        env.Bind("charAt", Builtin("charAt", args =>
        {
            var s = args[0].AssertType<StringValue>().Value;
            var i = (int)args[1].AssertType<IntValue>().Value;
            if (i < 0 || i >= s.Length)
                throw new InterpreterException(InterpreterMessages.CharAtIndexOutOfRange(i));
            return new CharValue(s[i]);
        }));

        env.Bind("substring", Builtin("substring", args =>
        {
            var s = args[0].AssertType<StringValue>().Value;
            var start = (int)args[1].AssertType<IntValue>().Value;
            var len = args.Length > 2 ? (int)args[2].AssertType<IntValue>().Value : s.Length - start;
            return new StringValue(s.Substring(start, len));
        }));

        env.Bind("stringToUpper", Builtin("stringToUpper", args => new StringValue(args[0].AssertType<StringValue>().Value.ToUpperInvariant())));
        env.Bind("stringToLower", Builtin("stringToLower", args => new StringValue(args[0].AssertType<StringValue>().Value.ToLowerInvariant())));

        // List
        env.Bind("listLength", Builtin("listLength", args => new IntValue(args[0].AssertType<ListValue>().Elements.Count)));
        env.Bind("head", Builtin("head", args =>
        {
            var list = args[0].AssertType<ListValue>();
            if (list.Elements.Count == 0)
                throw new InterpreterException(InterpreterMessages.HeadEmptyList);
            return list.Elements[0];
        }));
        env.Bind("tail", Builtin("tail", args =>
        {
            var list = args[0].AssertType<ListValue>();
            if (list.Elements.Count == 0)
                throw new InterpreterException(InterpreterMessages.TailEmptyList);
            return new ListValue(list.Elements.Skip(1).ToList());
        }));
        env.Bind("cons", Builtin("cons", args =>
        {
            var list = args[1].AssertType<ListValue>();
            return new ListValue([args[0], .. list.Elements]);
        }));
        env.Bind("append", Builtin("append", args =>
        {
            var a = args[0].AssertType<ListValue>();
            var b = args[1].AssertType<ListValue>();
            return new ListValue([.. a.Elements, .. b.Elements]);
        }));
        env.Bind("reverse", Builtin("reverse", args =>
        {
            var list = args[0].AssertType<ListValue>();
            return new ListValue(list.Elements.Reverse<RuntimeValue>().ToList());
        }));
        env.Bind("map", Builtin("map", args =>
        {
            var f = args[0].AssertType<FuncValue>();
            var list = args[1].AssertType<ListValue>();
            var results = new List<RuntimeValue>();
            foreach (var elem in list.Elements)
                results.Add(CallBuiltin(f, [elem]));
            return new ListValue(results);
        }));
        env.Bind("filter", Builtin("filter", args =>
        {
            var f = args[0].AssertType<FuncValue>();
            var list = args[1].AssertType<ListValue>();
            var results = new List<RuntimeValue>();
            foreach (var elem in list.Elements)
            {
                var result = CallBuiltin(f, [elem]).AssertType<BoolValue>();
                if (result.Value) results.Add(elem);
            }
            return new ListValue(results);
        }));
        env.Bind("foldl", Builtin("foldl", args =>
        {
            var f = args[0].AssertType<FuncValue>();
            var acc = args[1];
            var list = args[2].AssertType<ListValue>();
            foreach (var elem in list.Elements)
                acc = CallBuiltin(f, [acc, elem]);
            return acc;
        }));

        // Tuple
        env.Bind("fst", Builtin("fst", args =>
        {
            var tuple = args[0].AssertType<TupleValue>();
            if (tuple.Elements.Count < 1)
                throw new InterpreterException(InterpreterMessages.FstTupleTooSmall);
            return tuple.Elements[0];
        }));
        env.Bind("snd", Builtin("snd", args =>
        {
            var tuple = args[0].AssertType<TupleValue>();
            if (tuple.Elements.Count < 2)
                throw new InterpreterException(InterpreterMessages.SndTupleTooSmall);
            return tuple.Elements[1];
        }));

        // IO
        env.Bind("print", Builtin("print", args =>
        {
            Console.Write(args[0].Display());
            return UnitValue.Instance;
        }));
        env.Bind("println", Builtin("println", args =>
        {
            Console.WriteLine(args[0].Display());
            return UnitValue.Instance;
        }));
        env.Bind("printRaw", Builtin("printRaw", args =>
        {
            Console.Write(args[0] is StringValue sv ? sv.Value : args[0].Display());
            return UnitValue.Instance;
        }));
        env.Bind("printlnRaw", Builtin("printlnRaw", args =>
        {
            Console.WriteLine(args[0] is StringValue sv ? sv.Value : args[0].Display());
            return UnitValue.Instance;
        }));

        // Conversion
        env.Bind("intToString", Builtin("intToString", args => new StringValue(args[0].AssertType<IntValue>().Value.ToString())));
        env.Bind("floatToString", Builtin("floatToString", args => new StringValue(args[0].AssertType<FloatValue>().Value.ToString("G"))));
        env.Bind("stringToInt", Builtin("stringToInt", args =>
        {
            var s = args[0].AssertType<StringValue>().Value;
            return long.TryParse(s, out var v)
                ? new IntValue(v)
                : throw new InterpreterException(InterpreterMessages.StringToIntCannotParse(s));
        }));
        env.Bind("stringToFloat", Builtin("stringToFloat", args =>
        {
            var s = args[0].AssertType<StringValue>().Value;
            return double.TryParse(s, out var v)
                ? new FloatValue(v)
                : throw new InterpreterException(InterpreterMessages.StringToFloatCannotParse(s));
        }));

        // Misc
        env.Bind("id", Builtin("id", args => args[0]));
        env.Bind("not", Builtin("not", args => new BoolValue(!args[0].AssertType<BoolValue>().Value)));
        env.Bind("range", Builtin("range", args =>
        {
            var start = (int)args[0].AssertType<IntValue>().Value;
            var end = args.Length > 1 ? (int)args[1].AssertType<IntValue>().Value : start;
            var step = args.Length > 2 ? (int)args[2].AssertType<IntValue>().Value : 1;
            if (args.Length == 1) { start = 0; end = (int)args[0].AssertType<IntValue>().Value; step = 1; }
            var list = new List<RuntimeValue>();
            if (step > 0) for (var i = start; i < end; i += step) list.Add(new IntValue(i));
            else if (step < 0) for (var i = start; i > end; i += step) list.Add(new IntValue(i));
            return new ListValue(list);
        }));
    }

    private static BuiltinFuncValue Builtin(string name, Func<RuntimeValue[], RuntimeValue> impl) => new(name, impl);

    private static RuntimeValue CallBuiltin(FuncValue fv, RuntimeValue[] args)
    {
        if (args.Length != fv.Parameters.Count)
            throw new InterpreterException(InterpreterMessages.FunctionArityMismatch(fv.Parameters.Count, args.Length));

        var callEnv = new InterpreterEnvironment(fv.Closure);
        for (var i = 0; i < args.Length; i++)
            callEnv.Bind(fv.Parameters[i], args[i]);

        try
        {
            var interpreter = new EidosInterpreter();
            return interpreter.Eval(fv.Body, callEnv);
        }
        catch (ReturnException ret)
        {
            return ret.Value;
        }
    }
}
