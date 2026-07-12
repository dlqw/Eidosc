using Eidosc.Hir;

namespace Eidosc.Interpreter;

public sealed class EidosInterpreter
{
    public RuntimeValue Eval(HirNode node, InterpreterEnvironment env)
    {
        return node switch
        {
            HirLiteral lit => EvalLiteral(lit),
            HirVar v => EvalVar(v, env),
            HirBinOp bin => EvalBinOp(bin, env),
            HirUnaryOp un => EvalUnaryOp(un, env),
            HirCall call => EvalCall(call, env),
            HirIf ifExpr => EvalIf(ifExpr, env),
            HirLambda lambda => EvalLambda(lambda, env),
            HirBlock block => EvalBlock(block, env),
            HirTuple tuple => EvalTuple(tuple, env),
            HirList list => EvalList(list, env),
            HirMatch match => EvalMatch(match, env),
            HirReturn ret => throw new ReturnException(ret.Value != null ? Eval(ret.Value, env) : UnitValue.Instance),
            HirLoop loop => EvalLoop(loop, env),
            HirBreak br => throw new BreakException(br.Value != null ? Eval(br.Value, env) : UnitValue.Instance),
            HirContinue => throw new ContinueException(),
            HirFieldAccess field => EvalFieldAccess(field, env),
            HirIndexAccess idx => EvalIndexAccess(idx, env),
            HirListComprehension lc => EvalListComprehension(lc, env),
            HirPatternGuard => throw new InterpreterException(InterpreterMessages.PatternGuardsNotEvaluable),
            HirSequentialGuard => throw new InterpreterException(InterpreterMessages.SequentialGuardsNotEvaluable),
            _ => throw new InterpreterException(InterpreterMessages.UnsupportedHirNode(node.GetType().Name))
        };
    }

    private RuntimeValue EvalLiteral(HirLiteral lit)
    {
        return lit.LiteralKind switch
        {
            LiteralKind.Int => new IntValue((long)(lit.Value ?? 0)),
            LiteralKind.Float => new FloatValue((double)(lit.Value ?? 0.0)),
            LiteralKind.String => new StringValue((string?)lit.Value ?? ""),
            LiteralKind.Char => new CharValue((char)(lit.Value ?? '\0')),
            LiteralKind.Bool => new BoolValue((bool)(lit.Value ?? false)),
            LiteralKind.Unit => UnitValue.Instance,
            _ => throw new InterpreterException(InterpreterMessages.UnsupportedLiteralKind(lit.LiteralKind))
        };
    }

    private RuntimeValue EvalVar(HirVar v, InterpreterEnvironment env)
    {
        var value = env.Lookup(v.Name);
        if (value == null)
            throw new InterpreterException(InterpreterMessages.UndefinedVariable(v.Name));
        return value;
    }

    private RuntimeValue EvalBinOp(HirBinOp bin, InterpreterEnvironment env)
    {
        var left = Eval(bin.Left, env);

        // Short-circuit for && and ||
        if (bin.Operator == BinaryOp.And)
            return new BoolValue(left.AssertType<BoolValue>().Value && Eval(bin.Right, env).AssertType<BoolValue>().Value);
        if (bin.Operator == BinaryOp.Or)
            return new BoolValue(left.AssertType<BoolValue>().Value || Eval(bin.Right, env).AssertType<BoolValue>().Value);

        var right = Eval(bin.Right, env);

        return bin.Operator switch
        {
            BinaryOp.Add => EvalAdd(left, right),
            BinaryOp.Sub => new IntValue(left.AssertType<IntValue>().Value - right.AssertType<IntValue>().Value),
            BinaryOp.Mul => new IntValue(left.AssertType<IntValue>().Value * right.AssertType<IntValue>().Value),
            BinaryOp.Div => new IntValue(left.AssertType<IntValue>().Value / right.AssertType<IntValue>().Value),
            BinaryOp.Mod => new IntValue(left.AssertType<IntValue>().Value % right.AssertType<IntValue>().Value),
            BinaryOp.Eq => new BoolValue(EqualValues(left, right)),
            BinaryOp.Ne => new BoolValue(!EqualValues(left, right)),
            BinaryOp.Lt => new BoolValue(CompareValues(left, right) < 0),
            BinaryOp.Le => new BoolValue(CompareValues(left, right) <= 0),
            BinaryOp.Gt => new BoolValue(CompareValues(left, right) > 0),
            BinaryOp.Ge => new BoolValue(CompareValues(left, right) >= 0),
            BinaryOp.Concat => new StringValue(left.AssertType<StringValue>().Value + right.AssertType<StringValue>().Value),
            _ => throw new InterpreterException(InterpreterMessages.UnsupportedBinaryOperator(bin.Operator))
        };
    }

    private RuntimeValue EvalAdd(RuntimeValue left, RuntimeValue right)
    {
        if (left is IntValue li && right is IntValue ri)
            return new IntValue(li.Value + ri.Value);
        if (left is FloatValue lf && right is FloatValue rf)
            return new FloatValue(lf.Value + rf.Value);
        if (left is StringValue ls && right is StringValue rs)
            return new StringValue(ls.Value + rs.Value);
        throw new InterpreterException(InterpreterMessages.CannotAdd(left.GetType().Name, right.GetType().Name));
    }

    private RuntimeValue EvalUnaryOp(HirUnaryOp un, InterpreterEnvironment env)
    {
        var operand = Eval(un.Operand, env);
        return un.Operator switch
        {
            UnaryOp.Neg => operand switch
            {
                IntValue i => new IntValue(-i.Value),
                FloatValue f => new FloatValue(-f.Value),
                _ => throw new InterpreterException(InterpreterMessages.CannotNegate(operand.GetType().Name))
            },
            UnaryOp.Not => new BoolValue(!operand.AssertType<BoolValue>().Value),
            _ => throw new InterpreterException(InterpreterMessages.UnsupportedUnaryOperator(un.Operator))
        };
    }

    private RuntimeValue EvalCall(HirCall call, InterpreterEnvironment env)
    {
        var funcValue = Eval(call.Function, env);
        var args = call.Arguments.Select(a => Eval(a, env)).ToArray();

        return funcValue switch
        {
            FuncValue fv => CallFunction(fv, args),
            BuiltinFuncValue bf => bf.Impl(args),
            _ => throw new InterpreterException(InterpreterMessages.CannotCall(funcValue.GetType().Name))
        };
    }

    private RuntimeValue CallFunction(FuncValue fv, RuntimeValue[] args)
    {
        if (args.Length != fv.Parameters.Count)
            throw new InterpreterException(InterpreterMessages.FunctionArityMismatch(fv.Parameters.Count, args.Length));

        var callEnv = new InterpreterEnvironment(fv.Closure);
        for (var i = 0; i < args.Length; i++)
            callEnv.Bind(fv.Parameters[i], args[i]);

        try
        {
            return Eval(fv.Body, callEnv);
        }
        catch (ReturnException ret)
        {
            return ret.Value;
        }
    }

    private RuntimeValue EvalIf(HirIf ifExpr, InterpreterEnvironment env)
    {
        var cond = Eval(ifExpr.Condition, env).AssertType<BoolValue>();
        if (cond.Value)
            return Eval(ifExpr.ThenBranch, env);
        if (ifExpr.ElseBranch != null)
            return Eval(ifExpr.ElseBranch, env);
        return UnitValue.Instance;
    }

    private RuntimeValue EvalLambda(HirLambda lambda, InterpreterEnvironment env)
    {
        var paramNames = lambda.Parameters.Select(p => p.Name).ToList();
        return new FuncValue(paramNames, lambda.Body, env);
    }

    private RuntimeValue EvalBlock(HirBlock block, InterpreterEnvironment env)
    {
        var blockEnv = env.PushScope();
        RuntimeValue last = UnitValue.Instance;

        foreach (var stmt in block.Statements)
        {
            last = stmt switch
            {
                HirDeclStatement decl => EvalDeclStatement(decl, blockEnv),
                HirExprStatement expr => Eval(expr.Expression, blockEnv),
                HirAssignStatement assign => EvalAssignStatement(assign, blockEnv),
                _ => throw new InterpreterException(InterpreterMessages.UnsupportedStatementType(stmt.GetType().Name))
            };
        }

        if (block.Result != null)
            last = Eval(block.Result, blockEnv);

        return last;
    }

    private RuntimeValue EvalDeclStatement(HirDeclStatement decl, InterpreterEnvironment env)
    {
        return decl.Declaration switch
        {
            HirVal valDecl => EvalValDecl(valDecl, env),
            HirVarDecl varDecl => EvalVarDecl(varDecl, env),
            HirFunc funcDecl => EvalFuncDecl(funcDecl, env),
            _ => throw new InterpreterException(InterpreterMessages.UnsupportedDeclarationType(decl.Declaration.GetType().Name))
        };
    }

    private RuntimeValue EvalValDecl(HirVal valDecl, InterpreterEnvironment env)
    {
        var val = Eval(valDecl.Initializer, env);
        BindPattern(valDecl.Pattern, val, env);
        return val;
    }

    private RuntimeValue EvalVarDecl(HirVarDecl varDecl, InterpreterEnvironment env)
    {
        var val = Eval(varDecl.Initializer, env);
        BindPattern(varDecl.Pattern, val, env);
        return val;
    }

    private RuntimeValue EvalFuncDecl(HirFunc funcDecl, InterpreterEnvironment env)
    {
        var paramNames = funcDecl.Parameters.Select(p => p.Name).ToList();
        var funcValue = funcDecl.Body != null
            ? new FuncValue(paramNames, funcDecl.Body, env)
            : new FuncValue(paramNames, new HirLiteral { LiteralKind = LiteralKind.Unit }, env);
        env.Bind(funcDecl.Name, funcValue);
        return funcValue;
    }

    private RuntimeValue EvalAssignStatement(HirAssignStatement assign, InterpreterEnvironment env)
    {
        var val = Eval(assign.Value, env);
        return assign.Target switch
        {
            HirVar v => EvalAssignToVar(v, val, env),
            HirFieldAccess fa => EvalAssignToField(fa, val, env),
            HirIndexAccess ia => EvalAssignToIndex(ia, val, env),
            _ => throw new InterpreterException(InterpreterMessages.CannotAssignTo(assign.Target.GetType().Name))
        };
    }

    private RuntimeValue EvalAssignToVar(HirVar v, RuntimeValue val, InterpreterEnvironment env)
    {
        env.Set(v.Name, val);
        return val;
    }

    private RuntimeValue EvalAssignToField(HirFieldAccess fa, RuntimeValue val, InterpreterEnvironment env)
    {
        // Field assignment on records - limited support
        throw new InterpreterException(InterpreterMessages.FieldAssignmentUnsupported);
    }

    private RuntimeValue EvalAssignToIndex(HirIndexAccess ia, RuntimeValue val, InterpreterEnvironment env)
    {
        throw new InterpreterException(InterpreterMessages.IndexAssignmentUnsupported);
    }

    private void BindPattern(HirPattern pattern, RuntimeValue value, InterpreterEnvironment env)
    {
        switch (pattern)
        {
            case HirVarPattern vp when !vp.IsWildcard:
                env.Bind(vp.Name, value);
                break;
            case HirVarPattern vp when vp.IsWildcard:
                // Wildcard: discard
                break;
            case HirTuplePattern tp:
                if (value is not TupleValue tuple || tuple.Elements.Count != tp.Elements.Count)
                    throw new InterpreterException(InterpreterMessages.TupleDestructuringMismatch);
                for (var i = 0; i < tp.Elements.Count; i++)
                    BindPattern(tp.Elements[i], tuple.Elements[i], env);
                break;
            case HirCtorPattern cp:
                if (value is not CtorValue ctor || ctor.Name != cp.ConstructorName)
                    throw new InterpreterException(InterpreterMessages.ConstructorPatternMismatch(cp.ConstructorName));
                if (ctor.Fields.Count != cp.Fields.Count)
                    throw new InterpreterException(InterpreterMessages.ConstructorFieldCountMismatch);
                for (var i = 0; i < cp.Fields.Count; i++)
                    BindPattern(cp.Fields[i].Pattern, ctor.Fields[i], env);
                break;
            case HirListPattern lp:
                if (value is not ListValue list)
                    throw new InterpreterException(InterpreterMessages.ListDestructuringRequiresList);

                var minimumLength = lp.Elements.Count + lp.SuffixElements.Count;
                if (lp.HasRest)
                {
                    if (list.Elements.Count < minimumLength)
                        throw new InterpreterException(InterpreterMessages.ListLengthMismatchInDestructuring);
                }
                else if (list.Elements.Count != minimumLength)
                {
                    throw new InterpreterException(InterpreterMessages.ListLengthMismatchInDestructuring);
                }

                for (var i = 0; i < lp.Elements.Count; i++)
                    BindPattern(lp.Elements[i], list.Elements[i], env);

                for (var i = 0; i < lp.SuffixElements.Count; i++)
                {
                    var sourceIndex = list.Elements.Count - lp.SuffixElements.Count + i;
                    BindPattern(lp.SuffixElements[i], list.Elements[sourceIndex], env);
                }

                if (lp.HasRest && lp.RestPattern != null)
                {
                    var restCount = list.Elements.Count - minimumLength;
                    var rest = list.Elements
                        .Skip(lp.Elements.Count)
                        .Take(restCount)
                        .ToList();
                    BindPattern(lp.RestPattern, new ListValue(rest), env);
                }
                break;
            case HirAsPattern ap:
                env.Bind(ap.Name, value);
                BindPattern(ap.InnerPattern, value, env);
                break;
        }
    }

    private RuntimeValue EvalTuple(HirTuple tuple, InterpreterEnvironment env)
    {
        return new TupleValue(tuple.Elements.Select(e => Eval(e, env)).ToList());
    }

    private RuntimeValue EvalList(HirList list, InterpreterEnvironment env)
    {
        return new ListValue(list.Elements.Select(e => Eval(e, env)).ToList());
    }

    private RuntimeValue EvalMatch(HirMatch match, InterpreterEnvironment env)
    {
        var scrutinee = Eval(match.Scrutinee, env);

        foreach (var branch in match.Branches)
        {
            var matchEnv = env.PushScope();
            if (TryMatchPattern(branch.Pattern, scrutinee, matchEnv))
            {
                if (branch.Guard != null)
                {
                    var guardResult = Eval(branch.Guard, matchEnv).AssertType<BoolValue>();
                    if (!guardResult.Value) continue;
                }
                return Eval(branch.Body, matchEnv);
            }
        }

        throw new InterpreterException(InterpreterMessages.NonExhaustivePatternMatch);
    }

    private RuntimeValue EvalLoop(HirLoop loop, InterpreterEnvironment env)
    {
        var loopEnv = env.PushScope();
        while (true)
        {
            try
            {
                Eval(loop.Body, loopEnv);
            }
            catch (BreakException ex)
            {
                return ex.Value;
            }
            catch (ContinueException)
            {
                continue;
            }
        }
    }

    private RuntimeValue EvalFieldAccess(HirFieldAccess field, InterpreterEnvironment env)
    {
        var value = Eval(field.Target, env);
        if (value is CtorValue ctor)
        {
            for (var i = 0; i < ctor.Fields.Count; i++)
            {
                // Try to match by field name or by index if FieldName is numeric
                if (int.TryParse(field.FieldName, out var idx) && idx >= 0 && idx < ctor.Fields.Count)
                    return ctor.Fields[idx];
            }
        }
        if (value is TupleValue tuple)
        {
            if (int.TryParse(field.FieldName, out var idx) && idx >= 0 && idx < tuple.Elements.Count)
                return tuple.Elements[idx];
        }
        throw new InterpreterException(InterpreterMessages.CannotAccessField(field.FieldName, value.GetType().Name));
    }

    private RuntimeValue EvalIndexAccess(HirIndexAccess idx, InterpreterEnvironment env)
    {
        var target = Eval(idx.Target, env);
        var index = Eval(idx.Index, env);
        if (target is ListValue list && index is IntValue i)
        {
            if (i.Value >= 0 && i.Value < list.Elements.Count)
                return list.Elements[(int)i.Value];
            throw new InterpreterException(InterpreterMessages.IndexOutOfRange(i.Value));
        }
        if (target is TupleValue tuple && index is IntValue ti)
        {
            if (ti.Value >= 0 && ti.Value < tuple.Elements.Count)
                return tuple.Elements[(int)ti.Value];
            throw new InterpreterException(InterpreterMessages.TupleIndexOutOfRange(ti.Value));
        }
        throw new InterpreterException(InterpreterMessages.CannotIndex(target.GetType().Name, index.GetType().Name));
    }

    private RuntimeValue EvalListComprehension(HirListComprehension lc, InterpreterEnvironment env)
    {
        var results = new List<RuntimeValue>();
        EvalQualifiers(lc.Qualifiers, 0, env, () =>
        {
            results.Add(Eval(lc.Output, env));
        });
        return new ListValue(results);
    }

    private void EvalQualifiers(List<HirQualifier> qualifiers, int index, InterpreterEnvironment env, Action emit)
    {
        if (index >= qualifiers.Count)
        {
            emit();
            return;
        }

        var q = qualifiers[index];
        if (q.Kind == HirQualifierKind.Guard)
        {
            var guardResult = Eval(q.GuardExpression!, env).AssertType<BoolValue>();
            if (guardResult.Value)
                EvalQualifiers(qualifiers, index + 1, env, emit);
            return;
        }

        // Generator
        var source = Eval(q.GeneratorSource!, env);
        if (source is ListValue list)
        {
            foreach (var elem in list.Elements)
            {
                var innerEnv = env.PushScope();
                BindPattern(q.GeneratorPattern!, elem, innerEnv);
                EvalQualifiers(qualifiers, index + 1, innerEnv, emit);
            }
        }
    }

    #region Pattern Matching

    private bool TryMatchPattern(HirPattern pattern, RuntimeValue value, InterpreterEnvironment env)
    {
        return pattern switch
        {
            HirVarPattern vp when vp.IsWildcard => true,
            HirVarPattern vp => TryMatchVarPattern(vp, value, env),
            HirLiteralPattern lp => TryMatchLiteralPattern(lp, value),
            HirCtorPattern cp => TryMatchCtorPattern(cp, value, env),
            HirTuplePattern tp => TryMatchTuplePattern(tp, value, env),
            HirListPattern lp => TryMatchListPattern(lp, value, env),
            HirOrPattern op => TryMatchPattern(op.Left, value, env) || TryMatchPattern(op.Right, value, env),
            HirAndPattern ap => TryMatchPattern(ap.Left, value, env) && TryMatchPattern(ap.Right, value, env),
            HirNotPattern np => !TryMatchPattern(np.InnerPattern, value, env),
            HirAsPattern ap => TryMatchAsPattern(ap, value, env),
            HirRangePattern rp => TryMatchRangePattern(rp, value),
            HirViewPattern vp => false, // View patterns need function evaluation - not supported in interpreter
            _ => false
        };
    }

    private bool TryMatchVarPattern(HirVarPattern vp, RuntimeValue value, InterpreterEnvironment env)
    {
        env.Bind(vp.Name, value);
        return true;
    }

    private bool TryMatchLiteralPattern(HirLiteralPattern lp, RuntimeValue value)
    {
        return lp.Value switch
        {
            long l => value is IntValue iv && iv.Value == l,
            int i => value is IntValue iv && iv.Value == i,
            double d => value is FloatValue fv && Math.Abs(fv.Value - d) < double.Epsilon,
            float f => value is FloatValue fv && Math.Abs(fv.Value - f) < double.Epsilon,
            string s => value is StringValue sv && sv.Value == s,
            char c => value is CharValue cv && cv.Value == c,
            bool b => value is BoolValue bv && bv.Value == b,
            null => value is UnitValue,
            _ => EqualValues(value, FromLiteralValue(lp.Value))
        };
    }

    private bool TryMatchCtorPattern(HirCtorPattern cp, RuntimeValue value, InterpreterEnvironment env)
    {
        if (value is not CtorValue ctor) return false;
        if (ctor.Name != cp.ConstructorName) return false;
        if (ctor.Fields.Count != cp.Fields.Count) return false;

        for (var i = 0; i < cp.Fields.Count; i++)
        {
            if (!TryMatchPattern(cp.Fields[i].Pattern, ctor.Fields[i], env))
                return false;
        }
        return true;
    }

    private bool TryMatchTuplePattern(HirTuplePattern tp, RuntimeValue value, InterpreterEnvironment env)
    {
        if (value is not TupleValue tuple) return false;
        if (tuple.Elements.Count != tp.Elements.Count) return false;

        for (var i = 0; i < tp.Elements.Count; i++)
        {
            if (!TryMatchPattern(tp.Elements[i], tuple.Elements[i], env))
                return false;
        }
        return true;
    }

    private bool TryMatchListPattern(HirListPattern lp, RuntimeValue value, InterpreterEnvironment env)
    {
        if (value is not ListValue list) return false;

        var minimumLength = lp.Elements.Count + lp.SuffixElements.Count;
        if (lp.HasRest)
        {
            if (list.Elements.Count < minimumLength) return false;
        }
        else if (list.Elements.Count != minimumLength)
        {
            return false;
        }

        for (var i = 0; i < lp.Elements.Count; i++)
        {
            if (!TryMatchPattern(lp.Elements[i], list.Elements[i], env))
                return false;
        }

        for (var i = 0; i < lp.SuffixElements.Count; i++)
        {
            var sourceIndex = list.Elements.Count - lp.SuffixElements.Count + i;
            if (!TryMatchPattern(lp.SuffixElements[i], list.Elements[sourceIndex], env))
                return false;
        }

        if (lp.RestPattern != null)
        {
            var restCount = list.Elements.Count - minimumLength;
            var rest = new ListValue(list.Elements.Skip(lp.Elements.Count).Take(restCount).ToList());
            if (!TryMatchPattern(lp.RestPattern, rest, env))
                return false;
        }

        return true;
    }

    private bool TryMatchAsPattern(HirAsPattern ap, RuntimeValue value, InterpreterEnvironment env)
    {
        env.Bind(ap.Name, value);
        return TryMatchPattern(ap.InnerPattern, value, env);
    }

    private bool TryMatchRangePattern(HirRangePattern rp, RuntimeValue value)
    {
        var startVal = FromLiteralValue(rp.Start.Value);
        var endVal = FromLiteralValue(rp.End.Value);
        return value switch
        {
            IntValue iv => startVal is IntValue si && endVal is IntValue ei
                && iv.Value >= si.Value && iv.Value <= ei.Value,
            _ => false
        };
    }

    private static RuntimeValue FromLiteralValue(object? val)
    {
        return val switch
        {
            long l => new IntValue(l),
            int i => new IntValue(i),
            double d => new FloatValue(d),
            float f => new FloatValue(f),
            string s => new StringValue(s),
            char c => new CharValue(c),
            bool b => new BoolValue(b),
            _ => UnitValue.Instance
        };
    }

    #endregion

    #region Value Operations

    private static bool EqualValues(RuntimeValue a, RuntimeValue b) => (a, b) switch
    {
        (IntValue ai, IntValue bi) => ai.Value == bi.Value,
        (FloatValue af, FloatValue bf) => Math.Abs(af.Value - bf.Value) < double.Epsilon,
        (StringValue @as, StringValue bs) => @as.Value == bs.Value,
        (CharValue ac, CharValue bc) => ac.Value == bc.Value,
        (BoolValue ab, BoolValue bb) => ab.Value == bb.Value,
        (UnitValue, UnitValue) => true,
        (ListValue al, ListValue bl) => al.Elements.Count == bl.Elements.Count
            && al.Elements.Zip(bl.Elements).All(p => EqualValues(p.First, p.Second)),
        (TupleValue at, TupleValue bt) => at.Elements.Count == bt.Elements.Count
            && at.Elements.Zip(bt.Elements).All(p => EqualValues(p.First, p.Second)),
        (CtorValue ac, CtorValue bc) => ac.Name == bc.Name && ac.Fields.Count == bc.Fields.Count
            && ac.Fields.Zip(bc.Fields).All(p => EqualValues(p.First, p.Second)),
        _ => false
    };

    private static int CompareValues(RuntimeValue a, RuntimeValue b) => (a, b) switch
    {
        (IntValue ai, IntValue bi) => ai.Value.CompareTo(bi.Value),
        (FloatValue af, FloatValue bf) => af.Value.CompareTo(bf.Value),
        (StringValue @as, StringValue bs) => string.CompareOrdinal(@as.Value, bs.Value),
        (CharValue ac, CharValue bc) => ac.Value.CompareTo(bc.Value),
        _ => throw new InterpreterException(InterpreterMessages.CannotCompare(a.GetType().Name, b.GetType().Name))
    };

    #endregion
}
