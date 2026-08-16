using System.Text;

namespace Eidosc.Bindgen.Clang;

/// <summary>
/// C2E 源到源翻译（M7 切片 + 特性矩阵扩展）：把 C 函数体翻译为 Eidos 函数。
/// 支持子集：int/float 标量算术与比较、一元 -/!/解引用、局部变量（声明/赋值）、
/// if/else、while/for（经 loop+break 去糖）、return、同文件已翻译函数的调用、
/// 指针（T* 映射 RawPtr：经 std.Ffi 的 load/store/pointer_eq/null_pointer）、
/// union/struct 指针的成员访问（经自动生成的 c2e_* C shim 与 extern(c) 声明）、
/// 对外部 C 函数的调用（自动生成 extern(c) 声明，C 侧由调用方链接）。
/// C 定宽整数溢出不建模（统一提升为 Eidos Int/Float，值域内语义一致）；
/// 其余构造（取地址 &、指针算术/下标、switch/goto/三目、复合赋值、自增自减、
/// 跨类型混合算术、按值传递的聚合）不支持——所在函数跳过并注释。
/// </summary>
internal sealed class CBodyTranslator
{
    private readonly ClangApi _api;
    private ClangSession? _session;

    public CBodyTranslator(ClangApi api)
    {
        _api = api;
    }

    internal sealed record C2EResult(
        string Source,
        string NativeShimSource,
        IReadOnlyList<string> SkippedFunctions)
    {
        public bool IsEmpty => Source.Length == 0;
    }

    /// <summary>被翻译函数引用的 union/struct 成员访问（生成 accessor shim 与 extern 声明）。</summary>
    private sealed record RecordMemberAccess(string RecordSpelling, string RecordName, string Member, string MemberCType, string MemberEidosType);

    /// <summary>含 C float（32 位）的外部函数：double 中转 shim 规格。</summary>
    private sealed record FloatAbiShim(
        string CName,
        IReadOnlyList<(string Spelling, bool IsFloat)> Parameters,
        (string Spelling, bool IsFloat) Return);

    /// <summary>被翻译函数调用的外部 C 函数。</summary>
    private sealed record PendingExtern(string CName, string EidosName, IReadOnlyList<string> ParameterTypes, string ReturnType);

    public C2EResult Translate(string cSourcePath) =>
        Translate(cSourcePath, includePaths: null, defines: null, onlyFunctions: null);

    /// <summary>
    /// 带编译环境（-I/-D）的翻译入口：真实项目的 C 源几乎都依赖头搜索路径与配置宏。
    /// </summary>
    public C2EResult Translate(
        string cSourcePath,
        IReadOnlyList<string>? includePaths,
        IReadOnlyList<string>? defines,
        IReadOnlySet<string>? onlyFunctions = null)
    {
        using var session = new ClangSession(_api);
        _session = session;
        try
        {
            session.Parse(cSourcePath, includePaths: includePaths, defines: defines, skipFunctionBodies: false);
            var functions = new List<ClangCursor>();
            session.VisitChildren(session.RootCursor, (cursor, _, _) =>
            {
                if ((ClangCursorKind)cursor.Kind == ClangCursorKind.FunctionDecl)
                {
                    functions.Add(cursor);
                }
                else if ((ClangCursorKind)cursor.Kind is ClangCursorKind.StructDecl or ClangCursorKind.UnionDecl)
                {
                    CollectRecord(cursor);
                }
                else if ((ClangCursorKind)cursor.Kind == ClangCursorKind.VarDecl)
                {
                    _globals[_api.GetString(_api.GetCursorSpelling(cursor))] = cursor;
                }

                return ClangChildVisitResult.Continue;
            }, IntPtr.Zero);

            ResolveRecordFields();
            _resolvingRecords = false;

            var skipped = TranslateFunctions(functions, cSourcePath, out var source, out var shimSource, onlyFunctions);
            return new C2EResult(source, shimSource, skipped);
        }
        finally
        {
            _session = null;
        }
    }

    /// <summary>按值 C 结构体（raylib 的 Vector2/Rectangle/Color 等）→ Eidos 命名字段记录。</summary>
    private sealed record RecordField(string Name, string EidosType);

    private sealed record RecordSchema(string EidosName, string CSpelling)
    {
        public ClangCursor? Declaration { get; set; }
        public List<RecordField>? Fields { get; set; }
        public bool Mappable => Fields != null;
    }

    private readonly Dictionary<string, RecordSchema> _records = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedRecords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClangCursor> _globals = new(StringComparer.Ordinal);
    private readonly List<string> _usedGlobals = new();
    private readonly HashSet<string> _bannedCallees = [];
    private readonly HashSet<string> _translatableCandidates = new(StringComparer.Ordinal);
    private bool _resolvingRecords;

    private void CollectRecord(ClangCursor declaration)
    {
        var spelling = _api.GetString(_api.GetTypeSpelling(_api.GetCursorType(declaration)));
        var eidosName = RecordNameFromSpelling(spelling);
        if (string.IsNullOrWhiteSpace(eidosName))
        {
            return;
        }

        var isDefinition = Children(declaration).Any(static child => (ClangCursorKind)child.Kind == ClangCursorKind.FieldDecl);
        if (!_records.TryGetValue(eidosName, out var record))
        {
            _records[eidosName] = new RecordSchema(eidosName, spelling) { Declaration = declaration };
            return;
        }

        // 前向声明先出现时，用真正的定义（含字段）替换。
        if (isDefinition && record.Declaration is { } existing && !Children(existing).Any(static child => (ClangCursorKind)child.Kind == ClangCursorKind.FieldDecl))
        {
            record.Declaration = declaration;
        }
    }

    private void ResolveRecordFields()
    {
        _resolvingRecords = true;
        foreach (var name in _records.Keys.ToList())
        {
            ResolveRecordFields(name, []);
        }

        void ResolveRecordFields(string name, HashSet<string> visiting)
        {
            if (!_records.TryGetValue(name, out var record) || record.Fields != null || !visiting.Add(name))
            {
                return;
            }

            try
            {
                if (record.Declaration is not { } declaration)
                {
                    return;
                }

                var fields = new List<RecordField>();
                foreach (var child in Children(declaration))
                {
                    if ((ClangCursorKind)child.Kind != ClangCursorKind.FieldDecl)
                    {
                        continue;
                    }

                    var fieldMapping = MapType(_api.GetCursorType(child));
                    var fieldName = _api.GetString(_api.GetCursorSpelling(child));
                    if (fieldMapping == null)
                    {
                        // 含不可映射字段（数组等）的记录整体不可映射，指针指向时也一并跳过。
                        return;
                    }

                    if (IsValueRecord(fieldMapping) && !_records.ContainsKey(fieldMapping.EidosType))
                    {
                        return;
                    }

                    fields.Add(new RecordField(fieldName, fieldMapping.EidosType));
                }

                foreach (var field in fields)
                {
                    ResolveRecordFields(field.EidosType, visiting);
                }

                record.Fields = fields;
            }
            finally
            {
                visiting.Remove(name);
            }
        }
    }

    private static string RecordNameFromSpelling(string spelling)
    {
        var separator = spelling.IndexOf(' ');
        var name = separator > 0 ? spelling[(separator + 1)..] : spelling;
        return name.Trim();
    }

    /// <summary>引用文件级全局（可映射时登记使用，供模块级 mut 发射）。</summary>
    private void MarkGlobalUsed(string name)
    {
        if (_globals.TryGetValue(name, out var declaration) &&
            MapType(_api.GetCursorType(declaration)) != null &&
            !_usedGlobals.Contains(name))
        {
            _usedGlobals.Add(name);
        }
    }

    private FunctionContext BuildGlobalContext()
    {
        var context = new FunctionContext();
        foreach (var (name, declaration) in _globals)
        {
            var mapping = MapType(_api.GetCursorType(declaration));
            if (mapping != null)
            {
                context.VarTypes[name] = mapping;
            }
        }

        return context;
    }

    /// <summary>使用到的记录闭包：字段引用的嵌套记录一并发射声明。</summary>
    private List<string> CollectUsedRecordClosure()
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(_usedRecords);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!closure.Add(name) || !_records.TryGetValue(name, out var record) || record.Fields == null)
            {
                continue;
            }

            foreach (var field in record.Fields)
            {
                if (_records.ContainsKey(field.EidosType))
                {
                    queue.Enqueue(field.EidosType);
                }
            }
        }

        return closure.ToList();
    }

    private List<string> TranslateFunctions(
        List<ClangCursor> functions,
        string cSourcePath,
        out string source,
        out string shimSource,
        IReadOnlySet<string>? onlyFunctions = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Translated from C by the Eidos C2E translator. Do not edit.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();

        var skipped = new List<string>();
        var declaredByName = new Dictionary<string, ClangCursor>(StringComparer.Ordinal);
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            var name = _api.GetString(_api.GetCursorSpelling(function));
            declaredByName[name] = function;
            if (HasBody(function))
            {
                defined.Add(name);
            }
        }

        var state = new TranslationState(defined, declaredByName);
        foreach (var function in functions)
        {
            var name = _api.GetString(_api.GetCursorSpelling(function));
            if (!HasBody(function) || name == "main")
            {
                continue;
            }

            var parameterMappings = new List<CTypeMapping>();
            foreach (var child in Children(function))
            {
                if ((ClangCursorKind)child.Kind == ClangCursorKind.ParmDecl)
                {
                    parameterMappings.Add(MapType(_api.GetCursorType(child)) ?? UnsupportedMapping);
                }
            }

            state.FunctionParameters[name] = parameterMappings;
        }

        // 不动点重试：调用"定义了但翻译失败"的同文件函数会随失败传播逐轮跳过。
        _translatableCandidates.UnionWith(onlyFunctions ?? defined.Where(static name => name != "main"));
        var bodies = new List<string>();
        var banned = new HashSet<string>(StringComparer.Ordinal);
        var lastSkipReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            var bannedCount = banned.Count;
            _bannedCallees.Clear();
            _bannedCallees.UnionWith(banned);
            bodies.Clear();
            skipped.Clear();

            foreach (var function in functions)
            {
                var name = _api.GetString(_api.GetCursorSpelling(function));
                if (!HasBody(function) || name == "main")
                {
                    continue;
                }

                // 函数过滤：调用方（如打包脚本）只取指定入口；调用解析仍以全量声明为准。
                if (onlyFunctions != null && !onlyFunctions.Contains(name))
                {
                    continue;
                }

                if (banned.Contains(name))
                {
                    skipped.Add(name);
                    bodies.Add($"// SKIP {name}: {lastSkipReasons.GetValueOrDefault(name, "untranslated")}");
                    bodies.Add(string.Empty);
                    continue;
                }

                var translated = TryTranslateFunction(function, state);
                if (translated == null)
                {
                    // 空名（匿名/间接）不进 banned，避免毒化所有空 callee 调用。
                    if (!string.IsNullOrEmpty(name))
                    {
                        banned.Add(name);
                        lastSkipReasons[name] = SkipReason ?? "unknown";
                    }

                    skipped.Add(name);
                    bodies.Add($"// SKIP {name}: {SkipReason}");
                    bodies.Add(string.Empty);
                    continue;
                }

                bodies.Add(translated);
            }

            if (banned.Count == bannedCount)
            {
                break;
            }
        }

        // 值记录声明（含字段引用的传递闭包），先于函数与 extern。
        foreach (var recordName in CollectUsedRecordClosure())
        {
            var record = _records[recordName];
            sb.AppendLine($"{record.EidosName} :: type {{");
            sb.AppendLine($"    {string.Join($",{Environment.NewLine}    ", record.Fields!.Select(field => $"{field.Name} :: {field.EidosType}"))}");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (state.NeedsIntToFloat)
        {
            // Int→Float 显式转换经生成 C shim 提供（c2e_int_to_float_shim），
            // 避免与 runtime 自有的 eidos_int_to_float 符号冲突。
            sb.AppendLine("@[extern(c, name: \"c2e_int_to_float_shim\")]");
            sb.AppendLine("c2e_int_to_float :: Int -> Float need ffi;");
            sb.AppendLine();
        }

        // 文件级全局 → 模块级 mut 绑定（仅发射被引用且可映射者）。
        var globalContext = BuildGlobalContext();
        foreach (var globalName in _usedGlobals)
        {
            if (!_globals.TryGetValue(globalName, out var declaration))
            {
                continue;
            }

            var mapping = MapType(_api.GetCursorType(declaration));
            if (mapping == null)
            {
                continue;
            }

            var initChildren = ValueChildren(declaration);
            var init = initChildren.Count > 0
                ? TranslateExpression(initChildren[0], globalContext, state)
                : ZeroOf(mapping.EidosType, state);
            if (init == null)
            {
                init = ZeroOf(mapping.EidosType, state);
            }

            sb.AppendLine($"mut {globalName} := {init};");
            sb.AppendLine();
        }

        foreach (var externDeclaration in state.PendingExterns.Values)
        {
            sb.AppendLine($"@[extern(c, name: \"{externDeclaration.CName}\")]");
            var signature = externDeclaration.ParameterTypes.Count == 0
                ? $"Unit -> {externDeclaration.ReturnType}"
                : $"{string.Join(" -> ", externDeclaration.ParameterTypes)} -> {externDeclaration.ReturnType}";
            sb.AppendLine($"{externDeclaration.EidosName} :: {signature} need ffi;");
            sb.AppendLine();
        }

        foreach (var access in state.RecordMembers.Values)
        {
            var prefix = $"c2e_{access.RecordName}_{access.Member}";
            sb.AppendLine($"@[extern(c, name: \"{prefix}_get\")]");
            sb.AppendLine($"{prefix}_get :: RawPtr -> {access.MemberEidosType} need ffi;");
            sb.AppendLine($"@[extern(c, name: \"{prefix}_set\")]");
            sb.AppendLine($"{prefix}_set :: RawPtr -> {access.MemberEidosType} -> Unit need ffi;");
            sb.AppendLine();
        }

        if (state.NeedsFfiImport)
        {
            sb.AppendLine("import std.Ffi");
            sb.AppendLine();
        }

        foreach (var body in bodies)
        {
            sb.AppendLine(body);
        }

        source = sb.ToString();
        var shimText = BuildRecordMemberShimSource(cSourcePath, state.RecordMembers.Values) + BuildFloatShimSource(cSourcePath, state.FloatShims.Values);
        if (state.NeedsIntToFloat)
        {
            if (shimText.Length == 0)
            {
                shimText = "// <auto-generated>// Eidos C2E translator shims.// </auto-generated>" + Environment.NewLine;
            }

            shimText += Environment.NewLine + "double c2e_int_to_float_shim(long long v)" + Environment.NewLine +
                "{" + Environment.NewLine + "    return (double)v;" + Environment.NewLine + "}" + Environment.NewLine;
        }

        shimSource = shimText;
        return skipped;
    }

    /// <summary>C float ABI 中转 shim：Eidos Float(f64) ↔ C float，C 侧显式窄化。</summary>
    private string BuildFloatShimSource(string cSourcePath, IEnumerable<FloatAbiShim> shims)
    {
        var list = shims.ToList();
        if (list.Count == 0)
        {
            return string.Empty;
        }

        var shim = new StringBuilder();
        shim.AppendLine("// <auto-generated>");
        shim.AppendLine("// double<->float ABI shims emitted by the Eidos C2E translator.");
        shim.AppendLine("// </auto-generated>");
        shim.AppendLine("#include <stdint.h>");
        shim.AppendLine($"#include \"{cSourcePath.Replace('\\', '/')}\"");
        shim.AppendLine();
        foreach (var entry in list)
        {
            var eidosName = $"c2e_ext_{entry.CName}_f";
            var parameters = string.Join(", ", entry.Parameters.Select(static (parameter, index) =>
                parameter.IsFloat ? $"double p{index}" : $"{parameter.Spelling} p{index}"));
            if (parameters.Length == 0)
            {
                parameters = "void";
            }

            var callArguments = string.Join(", ", entry.Parameters.Select(static (parameter, index) =>
                parameter.IsFloat ? $"(float)p{index}" : $"p{index}"));
            var returnType = entry.Return.IsFloat ? "double" : entry.Return.Spelling;
            shim.AppendLine($"{returnType} {eidosName}({parameters})");
            shim.AppendLine("{");
            if (entry.Return.IsFloat)
            {
                shim.AppendLine($"    return (double){entry.CName}({callArguments});");
            }
            else if (entry.Return.Spelling == "void")
            {
                shim.AppendLine($"    {entry.CName}({callArguments});");
            }
            else
            {
                shim.AppendLine($"    return {entry.CName}({callArguments});");
            }

            shim.AppendLine("}");
        }

        return shim.ToString();
    }

    private string BuildRecordMemberShimSource(string cSourcePath, IEnumerable<RecordMemberAccess> accesses)
    {
        var list = accesses.ToList();
        if (list.Count == 0)
        {
            return string.Empty;
        }

        var shim = new StringBuilder();
        shim.AppendLine("// <auto-generated>");
        shim.AppendLine("// Accessor shims for union/struct member access emitted by the Eidos C2E translator.");
        shim.AppendLine("// </auto-generated>");
        shim.AppendLine($"#include \"{cSourcePath.Replace('\\', '/')}\"");
        shim.AppendLine();
        foreach (var access in list)
        {
            // 外部链接：Eidos 侧的 extern(c) 引用来自其他编译单元，static 会导致链接期 undefined symbol。
            var prefix = $"c2e_{access.RecordName}_{access.Member}";
            shim.AppendLine($"{access.MemberCType} {prefix}_get(void* __p) {{ return (({access.RecordSpelling}*)__p)->{access.Member}; }}");
            shim.AppendLine($"void {prefix}_set(void* __p, {access.MemberCType} __v) {{ (({access.RecordSpelling}*)__p)->{access.Member} = __v; }}");
        }

        return shim.ToString();
    }

    private string? SkipReason { get; set; }

    private bool HasBody(ClangCursor function) =>
        Children(function).Any(static child => child.Kind == ClangCursorKind2.CompoundStmt);

    private string? TryTranslateFunction(ClangCursor function, TranslationState state)
    {
        SkipReason = null;
        var children = Children(function);
        var body = children.FirstOrDefault(static c => c.Kind == ClangCursorKind2.CompoundStmt);

        // 参数类型：标量或指针（RawPtr），其余不支持。
        var paramTypes = new List<string>();
        var paramNames = new List<string>();
        var context = new FunctionContext();
        foreach (var child in children)
        {
            if ((ClangCursorKind)child.Kind != ClangCursorKind.ParmDecl)
            {
                continue;
            }

            var mapping = MapType(_api.GetCursorType(child));
            if (mapping == null)
            {
                SkipReason = $"parameter '{_api.GetString(_api.GetCursorSpelling(child))}' has unsupported type";
                return null;
            }

            paramTypes.Add(mapping.EidosType);
            var paramName = _api.GetString(_api.GetCursorSpelling(child));
            paramNames.Add(paramName);
            context.VarTypes[paramName] = mapping;
            context.ParameterNames.Add(paramName);
        }

        var returnMapping = MapType(_api.GetCursorResultType(function));
        if (returnMapping == null)
        {
            SkipReason = "return type is not a supported scalar or pointer";
            return null;
        }

        var returnType = returnMapping.EidosType;
        context.ReturnEidosType = returnType;
        var bodyText = TranslateStatements(Children(body), context, state);
        if (bodyText == null)
        {
            return null;
        }

        var signature = paramTypes.Count == 0
            ? $"Unit -> {returnType}"
            : $"{string.Join(" -> ", paramTypes)} -> {returnType}";
        var binders = paramNames.Count == 0
            ? "_ =>"
            : string.Join(" => ", paramNames) + " =>";
        // 兜底尾值按返回类型给出（记录/Float/指针各自的零值）。
        var tail = ZeroOf(returnType, state);

        var sb = new StringBuilder();
        sb.AppendLine($"{_api.GetString(_api.GetCursorSpelling(function))} :: {signature}");
        sb.AppendLine("{");
        sb.AppendLine($"    {binders} {{");
        sb.AppendLine(Indent(bodyText, 8));
        sb.AppendLine($"        {tail}");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string? TranslateStatements(List<ClangCursor> statements, FunctionContext context, TranslationState state)
    {
        var lines = new List<string>();
        foreach (var statement in statements)
        {
            var line = TranslateStatement(statement, context, state);
            if (line == null)
            {
                return null;
            }

            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>语句体子节点：花括号体取内部语句，无花括号单语句体保持原样。</summary>
    private List<ClangCursor> StatementBodyChildren(ClangCursor body) =>
        body.Kind == ClangCursorKind2.CompoundStmt ? Children(body) : [body];

    private string? TranslateStatement(ClangCursor statement, FunctionContext context, TranslationState state)
    {
        switch (statement.Kind)
        {
            case ClangCursorKind2.DeclStmt:
            {
                var lines = new List<string>();
                foreach (var varDecl in Children(statement))
                {
                    if ((ClangCursorKind)varDecl.Kind != ClangCursorKind.VarDecl)
                    {
                        SkipReason = "unsupported declaration in statement";
                        return null;
                    }

                    var varName = _api.GetString(_api.GetCursorSpelling(varDecl));
                    var varType = MapType(_api.GetCursorType(varDecl));
                    if (varType == null)
                    {
                        SkipReason = $"local '{varName}' has unsupported type";
                        return null;
                    }

                    // 记录类型的 VarDecl 首个子节点是 TypeRef（类型注解），过滤后再取初始化表达式。
                    var initChildren = ValueChildren(varDecl);
                    string? init;
                    if (initChildren.Count > 0)
                    {
                        init = TranslateExpression(initChildren[0], context, state);
                        if (init != null)
                        {
                            init = CoerceNumeric(init, EidosTypeOf(initChildren[0]), varType.EidosType, state);
                        }
                    }
                    else
                    {
                        init = ZeroOf(varType.EidosType, state);
                    }

                    if (init == null)
                    {
                        return null;
                    }

                    context.VarTypes[varName] = varType;
                    lines.Add($"mut {varName} := {init};");
                }

                return string.Join(Environment.NewLine, lines);
            }

            case ClangCursorKind2.CompoundStmt:
            {
                var inner = TranslateStatements(Children(statement), context, state);
                return inner == null ? null : inner;
            }

            case ClangCursorKind2.ReturnStmt:
            {
                var valueChildren = Children(statement);
                if (valueChildren.Count == 0)
                {
                    return string.Empty;
                }

                var value = TranslateExpression(valueChildren[0], context, state);
                if (value == null)
                {
                    return null;
                }

                value = CoerceNumeric(value, EidosTypeOf(valueChildren[0]), context.ReturnEidosType, state);
                return $"return {value};";
            }

            case ClangCursorKind2.IfStmt:
            {
                var parts = Children(statement);
                if (parts.Count is < 2 or > 3)
                {
                    SkipReason = "unsupported if form";
                    return null;
                }

                var condition = TranslateExpression(parts[0], context, state);
                if (condition == null)
                {
                    return null;
                }

                var thenBody = TranslateStatements(StatementBodyChildren(parts[1]), context, state);
                if (thenBody == null)
                {
                    return null;
                }

                string Brace(string inner) =>
                    $"{{{Environment.NewLine}{Indent(inner, 8)}{Environment.NewLine}    (){Environment.NewLine}}}";

                if (parts.Count == 3)
                {
                    var elseBody = parts[2].Kind == ClangCursorKind2.IfStmt
                        ? TranslateStatement(parts[2], context, state)
                        : TranslateStatements(StatementBodyChildren(parts[2]), context, state);
                    if (elseBody == null)
                    {
                        return null;
                    }

                    return $"if {condition} then{Brace(thenBody)} else{Brace(elseBody)};";
                }

                return $"if {condition} then{Brace(thenBody)} else {{ () }};";
            }

            case ClangCursorKind2.WhileStmt:
            {
                var parts = Children(statement);
                if (parts.Count != 2)
                {
                    SkipReason = "unsupported while form";
                    return null;
                }

                var condition = TranslateExpression(parts[0], context, state);
                if (condition == null)
                {
                    return null;
                }

                var body = TranslateStatements(StatementBodyChildren(parts[1]), context, state);
                if (body == null)
                {
                    return null;
                }

                return $"loop {{{Environment.NewLine}    if !({condition}) then break;{Environment.NewLine}{Indent(body, 4)}{Environment.NewLine}}};";
            }

            case ClangCursorKind2.ForStmt:
            {
                // init / cond / inc / body 去糖为声明 + loop。
                var parts = Children(statement);
                if (parts.Count != 4)
                {
                    SkipReason = "unsupported for form";
                    return null;
                }

                var init = TranslateStatement(parts[0], context, state);
                if (init == null || string.IsNullOrEmpty(init))
                {
                    SkipReason = "unsupported for-init";
                    return null;
                }

                var condition = TranslateExpression(parts[1], context, state);
                if (condition == null)
                {
                    return null;
                }

                var increment = TranslateExpression(parts[2], context, state, asStatement: true);
                if (increment == null)
                {
                    return null;
                }

                var body = TranslateStatements(StatementBodyChildren(parts[3]), context, state);
                if (body == null)
                {
                    return null;
                }

                var forSb = new StringBuilder();
                forSb.AppendLine(init);
                forSb.AppendLine("loop {");
                forSb.AppendLine($"    if !({condition}) then break;");
                forSb.AppendLine(Indent(body, 8));
                forSb.AppendLine($"    {increment};");
                forSb.AppendLine("    ()");
                forSb.AppendLine("}");
                return forSb.ToString();
            }

            default:
            {
                // 宏展开/括号/显式转换等包装语句（单子节点）透传处理。
                if (statement.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr or ClangCursorKind2.UnexposedStmt or ClangCursorKind2.CStyleCastExpr)
                {
                    var inner = ValueChildren(statement);
                    if (inner.Count == 1)
                    {
                        return TranslateStatement(inner[0], context, state);
                    }
                }

                // 自增/自减语句（x++ / ++x）去糖为 x := x ± 1。
                if (statement.Kind == ClangCursorKind2.UnaryOperator)
                {
                    var tokens = Tokenize(statement);
                    var op = tokens.FirstOrDefault(static token => token.Kind == ClangTokenKind.Punctuation).Spelling;
                    if (op is "++" or "--")
                    {
                        var operands = Children(statement);
                        var current = TranslateExpression(operands[0], context, state);
                        if (operands.Count == 1 && current != null)
                        {
                            var arithmetic = op == "++" ? $"{current} + 1" : $"{current} - 1";
                            return TryFormatStorageAssignment(operands[0], arithmetic, context, state) is { } updated
                                ? $"{updated};"
                                : null;
                        }
                    }
                }

                // 复合赋值语句（x += v）去糖为 x := x + v。
                if (statement.Kind == ClangCursorKind2.CompoundAssignOperator)
                {
                    var op = _api.GetString(_api.GetCursorSpelling(statement));
                    var operands = Children(statement);
                    if (operands.Count != 2 || op == null || !op.EndsWith("=", StringComparison.Ordinal) || op == "=")
                    {
                        SkipReason = "unsupported compound assignment";
                        return null;
                    }

                    var baseOp = op[..^1];
                    var current = TranslateExpression(operands[0], context, state);
                    var value = TranslateExpression(operands[1], context, state);
                    if (current == null || value == null)
                    {
                        return null;
                    }

                    return TryFormatStorageAssignment(operands[0], $"{current} {baseOp} ({value})", context, state) is { } combined
                        ? $"{combined};"
                        : null;
                }

                // 裸表达式语句（赋值/调用）。
                if (statement.Kind is ClangCursorKind2.BinaryOperator or ClangCursorKind2.CallExpr)
                {
                    return TranslateExpression(statement, context, state, asStatement: true) is { } expr
                        ? $"{expr};"
                        : null;
                }

                SkipReason = $"unsupported statement kind {statement.Kind}";
                return null;
            }
        }
    }

    private string? TranslateExpression(ClangCursor expression, FunctionContext context, TranslationState state, bool asStatement = false)
    {
        switch (expression.Kind)
        {
            case ClangCursorKind2.UnexposedExpr:
            {
                var inner = Children(expression);
                return inner.Count == 1 ? TranslateExpression(inner[0], context, state, asStatement) : null;
            }

            case ClangCursorKind2.ParenExpr:
            {
                // 括号承载 C 侧显式分组（(a+b)*c），Eidos 输出必须保留括号，
                // 否则跨优先级的分组会丢失。
                var inner = ValueChildren(expression);
                if (inner.Count != 1)
                {
                    SkipReason = "unsupported parenthesized form";
                    return null;
                }

                var text = TranslateExpression(inner[0], context, state, asStatement);
                return text == null ? null : $"({text})";
            }

            case ClangCursorKind2.CStyleCastExpr:
            {
                var castMapping = MapType(_api.GetCursorType(expression));
                var inner = ValueChildren(expression);
                if (inner.Count != 1 || castMapping == null)
                {
                    SkipReason = "unsupported cast";
                    return null;
                }

                var innerMapping = MapType(_api.GetCursorType(inner[0]));
                if (castMapping.EidosType == "RawPtr" ||
                    castMapping.EidosType == innerMapping?.EidosType)
                {
                    // 指针视角转换（如 (void*)0）、同 Eidos 类型（含记录、整数宽度变化）
                    // 均按值域内语义透明透传。
                    return TranslateExpression(inner[0], context, state, asStatement);
                }

                if (castMapping.EidosType == "Float" && innerMapping?.EidosType == "Int")
                {
                    state.NeedsIntToFloat = true;
                    var intValue = TranslateExpression(inner[0], context, state);
                    return intValue == null ? null : $"c2e_int_to_float({intValue})";
                }

                SkipReason = $"unsupported value-changing cast {innerMapping?.EidosType ?? "?"} -> {castMapping.EidosType}";
                return null;
            }

            case ClangCursorKind2.DeclRefExpr:
            {
                var name = _api.GetString(_api.GetCursorSpelling(expression));
                MarkGlobalUsed(name);
                return name;
            }

            case ClangCursorKind2.IntegerLiteral:
                return EvaluateLiteral(expression, integer: true);

            case ClangCursorKind2.FloatingLiteral:
                return EvaluateLiteral(expression, integer: false);

            case ClangCursorKind2.UnaryOperator:
                return TranslateUnaryOperator(expression, context, state);

            case ClangCursorKind2.MemberRefExpr:
                return TranslateMemberAccess(expression, context, state);

            case ClangCursorKind2.CompoundLiteralExpr:
            {
                // (Vector2){ x, y }：类型注解子节点已过滤，值即位置 InitList。
                var inner = ValueChildren(expression);
                return inner.Count == 1 ? TranslateExpression(inner[0], context, state) : null;
            }

            case ClangCursorKind2.InitListExpr:
            {
                // 位置初始化列表 → 记录构造；C 缺省字段补零值。
                var mapping = MapType(_api.GetCursorType(expression));
                if (mapping == null || !IsValueRecord(mapping) || !_records.TryGetValue(mapping.EidosType, out var record) || !record.Mappable)
                {
                    SkipReason = "init list for a non-record or unsupported type";
                    return null;
                }

                var values = ValueChildren(expression);
                if (values.Count > record.Fields!.Count)
                {
                    SkipReason = $"init list has more initializers than '{record.EidosName}' has fields";
                    return null;
                }

                var parts = new List<string>();
                for (var i = 0; i < record.Fields.Count; i++)
                {
                    var field = record.Fields[i];
                    if (i >= values.Count)
                    {
                        parts.Add($"{field.Name}: {ZeroOf(field.EidosType, state)}");
                        continue;
                    }

                    var value = TranslateExpression(values[i], context, state);
                    if (value == null)
                    {
                        return null;
                    }

                    // C 允许 { 0 } 跨类型零初始化（整型 0 写 float 字段）；
                    // 字面量可能被隐式转换节点包裹，先解包再判定。
                    if (field.EidosType == "Float" && IsIntegerLiteralValue(values[i]))
                    {
                        value += ".0";
                    }
                    else
                    {
                        value = CoerceNumeric(value, EidosTypeOf(values[i]), field.EidosType, state);
                    }

                    parts.Add($"{field.Name}: {value}");
                }

                return $"{record.EidosName} {{ {string.Join(", ", parts)} }}";
            }

            case ClangCursorKind2.ConditionalOperator:
            {
                // c ? a : b → Eidos if 表达式；Int 条件归一为比较，两臂做数值提升。
                var operands = ValueChildren(expression);
                if (operands.Count != 3)
                {
                    SkipReason = "unsupported conditional form";
                    return null;
                }

                var condition = TranslateExpression(operands[0], context, state);
                var thenText = TranslateExpression(operands[1], context, state);
                var elseText = TranslateExpression(operands[2], context, state);
                if (condition == null || thenText == null || elseText == null)
                {
                    return null;
                }

                if (EidosTypeOf(operands[0]) == "Int")
                {
                    condition = $"({condition} != 0)";
                }

                var thenType = EidosTypeOf(operands[1]);
                var elseType = EidosTypeOf(operands[2]);
                if (thenType == "Float" && elseType == "Int")
                {
                    elseText = CoerceNumeric(elseText, elseType, "Float", state);
                }
                else if (elseType == "Float" && thenType == "Int")
                {
                    thenText = CoerceNumeric(thenText, thenType, "Float", state);
                }

                return $"(if {condition} then {thenText} else {elseText})";
            }

            case ClangCursorKind2.BinaryOperator:
            {
                var op = _api.GetString(_api.GetCursorSpelling(expression));
                var operands = Children(expression);
                if (operands.Count != 2)
                {
                    SkipReason = $"unsupported operand count for '{op}'";
                    return null;
                }

                if (op == "=")
                {
                    if (asStatement)
                    {
                        return TranslateAssignment(operands[0], operands[1], context, state);
                    }

                    SkipReason = "assignment used in expression context";
                    return null;
                }

                if (op is "==" or "!=" &&
                    (IsPointerTyped(operands[0]) || IsPointerTyped(operands[1])))
                {
                    return TranslatePointerComparison(op, operands[0], operands[1], context, state);
                }

                var left = TranslateExpression(operands[0], context, state);
                var right = TranslateExpression(operands[1], context, state);
                if (left == null || right == null)
                {
                    return null;
                }

                // C 常规算术转换：算术/比较运算两侧 Int/Float 混用时提升 Int 侧。
                if (op is "+" or "-" or "*" or "/" or "%" or "<" or "<=" or ">" or ">=" or "==" or "!=")
                {
                    var leftType = EidosTypeOf(operands[0]);
                    var rightType = EidosTypeOf(operands[1]);
                    if (leftType == "Float" && rightType == "Int")
                    {
                        right = CoerceNumeric(right, rightType, "Float", state);
                    }
                    else if (rightType == "Float" && leftType == "Int")
                    {
                        left = CoerceNumeric(left, leftType, "Float", state);
                    }
                }

                return op switch
                {
                    "+" or "-" or "*" or "/" or "%" or "<" or "<=" or ">" or ">=" or "==" or "!=" => $"{left} {op} {right}",
                    _ when op == "&&" => $"{left} && {right}",
                    _ when op == "||" => $"{left} || {right}",
                    _ => Skip(op),
                };

                string? Skip(string unsupported)
                {
                    SkipReason = $"unsupported operator '{unsupported}'";
                    return null;
                }
            }

            case ClangCursorKind2.CallExpr:
            {
                var operands = Children(expression);
                if (operands.Count == 0)
                {
                    SkipReason = "unsupported call form";
                    return null;
                }

                // callee 可能被 UnexposedExpr/MemberRefExpr（宏或内联包装）包裹：解包取真实拼写。
                var calleeCursor = operands[0];
                var callee = _api.GetString(_api.GetCursorSpelling(calleeCursor));
                while (string.IsNullOrEmpty(callee) &&
                       calleeCursor.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr)
                {
                    var innerCallee = ValueChildren(calleeCursor);
                    if (innerCallee.Count != 1)
                    {
                        break;
                    }

                    calleeCursor = innerCallee[0];
                    callee = _api.GetString(_api.GetCursorSpelling(calleeCursor));
                }

                if (string.IsNullOrEmpty(callee))
                {
                    // 经函数指针的间接调用（GL loader 等）：无被调名，本轮不支持。
                    SkipReason = "indirect (function pointer) call";
                    return null;
                }

                if (state.DefinedNames.Contains(callee))
                {
                    // 裸调用仅限"会真正发射 Eidos 体"的函数（入选过滤集且未被禁）；
                    // 其余同文件定义（被过滤出去或翻译失败）一律回退 extern(c) 调地板 C 符号。
                    if (_translatableCandidates.Contains(callee) && !_bannedCallees.Contains(callee) &&
                        state.DeclaredFunctions.ContainsKey(callee))
                    {
                        return TranslateCall(callee, operands.Skip(1), context, state);
                    }

                    return TranslateExternalCall(callee, operands.Skip(1), context, state);
                }

                return TranslateExternalCall(callee, operands.Skip(1), context, state);
            }

            default:
            {
                SkipReason = expression.Kind switch
                {
                    ClangCursorKind2.StringLiteral => "string literal",
                    ClangCursorKind2.ConditionalOperator => "ternary conditional",
                    ClangCursorKind2.ArraySubscriptExpr => "array subscript",
                    _ => $"unsupported expression kind {expression.Kind}"
                };
                return null;
            }
        }
    }

    private string? TranslateUnaryOperator(ClangCursor expression, FunctionContext context, TranslationState state)
    {
        var tokens = Tokenize(expression);
        var op = tokens.FirstOrDefault(static token => token.Kind == ClangTokenKind.Punctuation).Spelling;
        var operands = Children(expression);
        if (op == null || operands.Count != 1)
        {
            SkipReason = "unsupported unary operator";
            return null;
        }

        var operand = operands[0];
        if (op == "*")
        {
            if (!TryResolveBaseVariable(operand, context, out var varType) || varType.ElementEidosType == null)
            {
                SkipReason = "dereference of a pointer without a supported element type";
                return null;
            }

            var pointer = TranslateExpression(operand, context, state);
            return pointer == null
                ? null
                : $"Ffi.load[{varType.ElementEidosType}]({pointer})";
        }

        if (op == "!")
        {
            var operandText = TranslateExpression(operand, context, state);
            if (operandText == null)
            {
                return null;
            }

            if (IsPointerTyped(operand))
            {
                state.NeedsFfiImport = true;
                return $"Ffi.pointer_eq({operandText})(Ffi.null_pointer())";
            }

            return $"({operandText} == 0)";
        }

        if (op == "-")
        {
            var operandText = TranslateExpression(operand, context, state);
            return operandText == null ? null : $"-{operandText}";
        }

        SkipReason = $"unsupported unary operator '{op}'";
        return null;
    }

    private string? TranslateMemberAccess(ClangCursor expression, FunctionContext context, TranslationState state)
    {
        var member = _api.GetString(_api.GetCursorSpelling(expression));
        var operands = Children(expression);
        if (operands.Count != 1)
        {
            SkipReason = "unsupported member access form";
            return null;
        }

        var baseCursor = operands[0];

        // 值记录字段读取（含链式：GetFontDefault().texture.id、rec.x 等）。
        if (MapType(_api.GetCursorType(baseCursor)) is { } baseMapping && IsValueRecord(baseMapping))
        {
            var memberMapping = MapType(_api.GetCursorType(expression));
            if (memberMapping == null)
            {
                SkipReason = $"member '{member}' has an unsupported type";
                return null;
            }

            var baseText = TranslateExpression(baseCursor, context, state);
            return baseText == null ? null : $"{baseText}.{member}";
        }

        // 指针记录成员（s->field）走 M4 式 accessor 桥。
        if (!TryResolveBaseVariable(baseCursor, context, out var varType) || varType.RecordName == null)
        {
            SkipReason = "member access on a non-record-pointer base";
            return null;
        }

        var memberMapping2 = MapType(_api.GetCursorType(expression));
        if (memberMapping2 == null)
        {
            SkipReason = $"member '{member}' has an unsupported type";
            return null;
        }

        var pointer = TranslateExpression(baseCursor, context, state);
        if (pointer == null)
        {
            return null;
        }

        var access = new RecordMemberAccess(
            varType.RecordSpelling!,
            varType.RecordName!,
            member,
            _api.GetString(_api.GetTypeSpelling(_api.GetCursorType(expression))),
            memberMapping2.EidosType);
        state.RecordMembers[(varType.RecordName!, member)] = access;
        return $"c2e_{varType.RecordName}_{member}_get({pointer})";
    }

    private string? TranslateAssignment(ClangCursor target, ClangCursor value, FunctionContext context, TranslationState state)
    {
        // *p = v → Ffi.store[T](p)(v)；u->m = v → c2e_<R>_<m>_set(u)(v)；其余为普通赋值。
        if (target.Kind == ClangCursorKind2.UnaryOperator)
        {
            var tokens = Tokenize(target);
            var op = tokens.FirstOrDefault(static token => token.Kind == ClangTokenKind.Punctuation).Spelling;
            var targetOperands = Children(target);
            if (op != "*" || targetOperands.Count != 1 ||
                !TryResolveBaseVariable(targetOperands[0], context, out var varType) ||
                varType.ElementEidosType == null)
            {
                SkipReason = "unsupported assignment target dereference";
                return null;
            }

            var pointer = TranslateExpression(targetOperands[0], context, state);
            var assigned = TranslateExpression(value, context, state);
            if (pointer == null || assigned == null)
            {
                return null;
            }

            state.NeedsFfiImport = true;
            return $"Ffi.store[{varType.ElementEidosType}]({pointer})({assigned})";
        }

        if (target.Kind == ClangCursorKind2.MemberRefExpr)
        {
            // 值记录局部字段写：s.x = v → s := s.{x: v}（嵌套路径字段本轮不支持）。
            var targetOperands = Children(target);
            if (targetOperands.Count == 1 &&
                MapType(_api.GetCursorType(targetOperands[0])) is { } targetBaseMapping &&
                IsValueRecord(targetBaseMapping))
            {
                if (targetOperands[0].Kind != ClangCursorKind2.DeclRefExpr)
                {
                    SkipReason = "nested member assignment target";
                    return null;
                }

                var member = _api.GetString(_api.GetCursorSpelling(target));
                var assigned = TranslateExpression(value, context, state);
                if (assigned == null)
                {
                    return null;
                }

                assigned = CoerceNumeric(assigned, EidosTypeOf(value), MapType(_api.GetCursorType(target))?.EidosType, state);
                var name = _api.GetString(_api.GetCursorSpelling(targetOperands[0]));
                return $"{name} := {name}.{{{member}: {assigned}}}";
            }

            var member2 = _api.GetString(_api.GetCursorSpelling(target));
            var pointerOperands = Children(target);
            if (pointerOperands.Count != 1 ||
                !TryResolveBaseVariable(pointerOperands[0], context, out var varType) ||
                varType.RecordName == null)
            {
                SkipReason = "unsupported member assignment target";
                return null;
            }

            var memberMapping = MapType(_api.GetCursorType(target));
            if (memberMapping == null)
            {
                SkipReason = $"member '{member2}' has an unsupported type";
                return null;
            }

            var pointer = TranslateExpression(pointerOperands[0], context, state);
            var assigned2 = TranslateExpression(value, context, state);
            if (pointer == null || assigned2 == null)
            {
                return null;
            }

            assigned2 = CoerceNumeric(assigned2, EidosTypeOf(value), memberMapping.EidosType, state);

            state.RecordMembers[(varType.RecordName!, member2)] = new RecordMemberAccess(
                varType.RecordSpelling!,
                varType.RecordName!,
                member2,
                _api.GetString(_api.GetTypeSpelling(_api.GetCursorType(target))),
                memberMapping.EidosType);
            return $"c2e_{varType.RecordName}_{member2}_set({pointer})({assigned2})";
        }

        var valueText = TranslateExpression(value, context, state);
        if (valueText == null)
        {
            return null;
        }

        if (target.Kind == ClangCursorKind2.DeclRefExpr)
        {
            var targetName = _api.GetString(_api.GetCursorSpelling(target));
            if (context.VarTypes.TryGetValue(targetName, out var targetMapping))
            {
                valueText = CoerceNumeric(valueText, EidosTypeOf(value), targetMapping.EidosType, state);
            }
        }

        return TryFormatStorageAssignment(target, valueText, context, state);
    }

    /// <summary>把赋值格式化为 Eidos 可存储目标：mut 局部/模块级全局直接重绑；参数不可变，整体跳过。</summary>
    private string? TryFormatStorageAssignment(ClangCursor target, string valueText, FunctionContext context, TranslationState state)
    {
        if (target.Kind == ClangCursorKind2.DeclRefExpr)
        {
            var name = _api.GetString(_api.GetCursorSpelling(target));
            if (context.VarTypes.TryGetValue(name, out _))
            {
                if (context.ParameterNames.Contains(name))
                {
                    SkipReason = $"mutation of parameter '{name}'";
                    return null;
                }

                return $"{name} := {valueText}";
            }

            // 文件级全局 → 模块级 mut 绑定。
            if (_globals.TryGetValue(name, out var declaration) && MapType(_api.GetCursorType(declaration)) != null)
            {
                MarkGlobalUsed(name);
                return $"{name} := {valueText}";
            }

            SkipReason = $"assignment to '{name}' outside the supported local scope";
            return null;
        }

        SkipReason = "unsupported assignment target";
        return null;
    }

    private string? TranslatePointerComparison(
        string op,
        ClangCursor left,
        ClangCursor right,
        FunctionContext context,
        TranslationState state)
    {
        // 指针字面量（0 / (void*)0 形态的 NULL）翻成 null_pointer；其余按 pointer_eq 比较。
        string? TranslateOperand(ClangCursor operand)
        {
            switch (ClassifyPointerLiteral(operand))
            {
                case PointerLiteralKind.NullLiteral:
                    state.NeedsFfiImport = true;
                    return "Ffi.null_pointer()";
                case PointerLiteralKind.NonNullLiteral:
                    SkipReason = "non-null integer used as a pointer value";
                    return null;
                default:
                    return TranslateExpression(operand, context, state);
            }
        }

        var leftText = TranslateOperand(left);
        var rightText = TranslateOperand(right);
        if (leftText == null || rightText == null)
        {
            return null;
        }

        state.NeedsFfiImport = true;
        var equality = $"Ffi.pointer_eq({leftText})({rightText})";
        return op == "!=" ? $"!({equality})" : equality;
    }

    /// <summary>占位映射：调用实参强转查询失败时按普通标量处理。</summary>
    private static readonly CTypeMapping UnsupportedMapping = new("Int", null, null, null);

    private string? TranslateCall(string callee, IEnumerable<ClangCursor> arguments, FunctionContext context, TranslationState state)
    {
        var parameterMappings = state.FunctionParameters.TryGetValue(callee, out var mappings)
            ? mappings
            : [];
        var argumentList = arguments.ToList();
        var argumentTexts = new List<string>();
        for (var i = 0; i < argumentList.Count; i++)
        {
            var translated = TranslateCallArgument(
                argumentList[i],
                i < parameterMappings.Count ? parameterMappings[i] : null,
                context,
                state);
            if (translated == null)
            {
                return null;
            }

            argumentTexts.Add(translated);
        }

        return $"{callee}({string.Join(", ", argumentTexts)})";
    }

    /// <summary>调用实参翻译：指针参数位置的 0/(T*)0 视为 NULL 字面量。</summary>
    private string? TranslateCallArgument(ClangCursor argument, CTypeMapping? parameter, FunctionContext context, TranslationState state)
    {
        if (parameter?.EidosType == "RawPtr")
        {
            switch (ClassifyPointerLiteral(argument))
            {
                case PointerLiteralKind.NullLiteral:
                    state.NeedsFfiImport = true;
                    return "Ffi.null_pointer()";
                case PointerLiteralKind.NonNullLiteral:
                    SkipReason = "non-null integer used as a pointer value";
                    return null;
            }
        }

        var translated = TranslateExpression(argument, context, state);
        if (translated == null)
        {
            return null;
        }

        return CoerceNumeric(translated, EidosTypeOf(argument), parameter?.EidosType, state);
    }

    private string? TranslateExternalCall(string callee, IEnumerable<ClangCursor> arguments, FunctionContext context, TranslationState state)
    {
        if (!state.DeclaredFunctions.TryGetValue(callee, out var declaration) ||
            _api.CursorIsVariadic(declaration) != 0)
        {
            SkipReason = $"call to untranslated function '{callee}'";
            return null;
        }

        var functionType = _api.GetCursorType(declaration);
        var arity = _api.GetNumArgTypes(functionType);
        var argumentList = arguments.ToList();
        if (arity != argumentList.Count)
        {
            SkipReason = $"call to '{callee}' does not match its declaration";
            return null;
        }

        var parameterMappings = new List<CTypeMapping>((int)arity);
        for (var i = 0; i < arity; i++)
        {
            var mapping = MapType(_api.GetArgType(functionType, (uint)i));
            if (mapping == null)
            {
                SkipReason = $"parameter {i + 1} of '{callee}' has an unsupported type";
                return null;
            }

            parameterMappings.Add(mapping);
        }

        var returnMapping = MapType(_api.GetResultType(functionType));
        if (returnMapping == null)
        {
            SkipReason = $"return type of '{callee}' is not a supported scalar or pointer";
            return null;
        }

        // 按值结构体跨 extern(c) ABI（寄存器/内存小结构传递）与 Eidos 记录指针约定不兼容，本轮挡住。
        if (IsValueRecord(returnMapping) || parameterMappings.Any(static mapping => IsValueRecord(mapping)))
        {
            SkipReason = $"extern '{callee}' passes a struct by value across the C ABI";
            return null;
        }

        // C float（32 位）参数/返回值与 Eidos Float（f64）extern ABI 不兼容：
        // 生成 double 中转 shim（c2e_ext_<name>_f），C 侧窄化转换。
        var floatShimNeeded = false;
        var rawReturn = _api.GetCanonicalType(_api.GetResultType(functionType));
        if ((ClangTypeKind)rawReturn.Kind == ClangTypeKind.Float)
        {
            floatShimNeeded = true;
        }

        var parameterSpells = new List<(string Spelling, bool IsFloat)>((int)arity);
        for (var i = 0; i < arity; i++)
        {
            var rawArg = _api.GetCanonicalType(_api.GetArgType(functionType, (uint)i));
            var isFloat = (ClangTypeKind)rawArg.Kind == ClangTypeKind.Float;
            floatShimNeeded |= isFloat;
            parameterSpells.Add((_api.GetString(_api.GetTypeSpelling(_api.GetArgType(functionType, (uint)i))), isFloat));
        }

        if (floatShimNeeded && !state.FloatShims.TryGetValue(callee, out _))
        {
            state.FloatShims[callee] = new FloatAbiShim(
                callee,
                parameterSpells,
                (_api.GetString(_api.GetTypeSpelling(_api.GetResultType(functionType))), (ClangTypeKind)rawReturn.Kind == ClangTypeKind.Float));
        }

        if (!state.PendingExterns.TryGetValue(callee, out var pending))
        {
            // float ABI shim 生效时，Eidos 侧 extern 绑定到 shim 符号（c2e_ext_<name>_f）。
            pending = new PendingExtern(
                floatShimNeeded ? $"c2e_ext_{callee}_f" : callee,
                floatShimNeeded ? $"c2e_ext_{callee}_f" : $"c2e_ext_{callee}",
                parameterMappings.Select(static mapping => mapping.EidosType).ToList(),
                returnMapping.EidosType);
            state.PendingExterns[callee] = pending;
        }

        var argumentTexts = new List<string>(argumentList.Count);
        for (var i = 0; i < argumentList.Count; i++)
        {
            var translated = TranslateCallArgument(argumentList[i], parameterMappings[i], context, state);
            if (translated == null)
            {
                return null;
            }

            argumentTexts.Add(translated);
        }

        return $"{pending.EidosName}({string.Join(", ", argumentTexts)})";
    }

    private bool IsPointerTyped(ClangCursor operand) =>
        (ClangTypeKind)_api.GetCanonicalType(_api.GetCursorType(operand)).Kind == ClangTypeKind.Pointer;

    private enum PointerLiteralKind
    {
        NotLiteral,
        NullLiteral,
        NonNullLiteral
    }

    /// <summary>识别出现在指针位置的 0 / (T*)0（NULL）与非零整数字面量。</summary>
    private PointerLiteralKind ClassifyPointerLiteral(ClangCursor operand)
    {
        if (!IsPointerLiteral(operand))
        {
            return PointerLiteralKind.NotLiteral;
        }

        var current = operand;
        while (current.Kind != ClangCursorKind2.IntegerLiteral)
        {
            var inner = Children(current);
            if (inner.Count != 1)
            {
                return PointerLiteralKind.NotLiteral;
            }

            current = inner[0];
        }

        var result = _api.CursorEvaluate(current);
        if (result == IntPtr.Zero)
        {
            return PointerLiteralKind.NotLiteral;
        }

        try
        {
            return _api.EvalResultGetAsLongLong(result) == 0
                ? PointerLiteralKind.NullLiteral
                : PointerLiteralKind.NonNullLiteral;
        }
        finally
        {
            _api.EvalResultDispose(result);
        }
    }

    /// <summary>结构判定：cursor 是否为指针位置的整数字面量（含转换/包裹）。</summary>
    private bool IsPointerLiteral(ClangCursor operand)
    {
        if (!IsPointerTyped(operand))
        {
            return false;
        }

        var current = operand;
        while (true)
        {
            if (current.Kind == ClangCursorKind2.IntegerLiteral)
            {
                return true;
            }

            if (current.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.CStyleCastExpr)
            {
                var inner = Children(current);
                if (inner.Count != 1)
                {
                    return false;
                }

                current = inner[0];
                continue;
            }

            return false;
        }
    }

    /// <summary>把（可能被 Unexposed/转换包裹的）表达式解析到已声明变量，取其类型信息。</summary>
    private bool TryResolveBaseVariable(ClangCursor operand, FunctionContext context, out CTypeMapping mapping)
    {
        mapping = null!;
        var current = operand;
        while (true)
        {
            if (current.Kind == ClangCursorKind2.DeclRefExpr)
            {
                var name = _api.GetString(_api.GetCursorSpelling(current));
                if (context.VarTypes.TryGetValue(name, out mapping!))
                {
                    return true;
                }

                return false;
            }

            if (current.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.CStyleCastExpr)
            {
                var inner = Children(current);
                if (inner.Count != 1)
                {
                    return false;
                }

                current = inner[0];
                continue;
            }

            return false;
        }
    }

    private string? EvaluateLiteral(ClangCursor literal, bool integer)
    {
        var result = _api.CursorEvaluate(literal);
        if (result == IntPtr.Zero)
        {
            SkipReason = "literal evaluation failed";
            return null;
        }

        try
        {
            var kind = _api.EvalResultGetKind(result);
            if (integer)
            {
                return _api.EvalResultGetAsLongLong(result).ToString();
            }

            if ((ClangEvalResultKind)kind != ClangEvalResultKind.Float)
            {
                SkipReason = $"unsupported literal evaluation kind {kind}";
                return null;
            }

            // 浮点结果必须走 getAsDouble：getAsStr 仅对 Int/Float 之外的 kind 有定义
            //（clang-c/Index.h），对 Float 会把 double 位模式当 char* 返回。
            return NormalizeFloat(
                _api.EvalResultGetAsDouble(result).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            _api.EvalResultDispose(result);
        }
    }

    private static string NormalizeFloat(string text)
    {
        if (text.Contains('e') || text.Contains('E') || text.Contains('.'))
        {
            return text;
        }

        return text + ".0";
    }

    private static string DefaultZero(string eidosType) => eidosType switch
    {
        "Float" => "0.0",
        "RawPtr" => "Ffi.null_pointer()",
        _ => "0"
    };

    /// <summary>标量与指针的 Eidos 类型映射；指针额外携带元素类型 / 记录（union、struct）事实。</summary>
    private sealed record CTypeMapping(string EidosType, string? ElementEidosType, string? RecordSpelling, string? RecordName);

    private CTypeMapping? MapType(ClangType type)
    {
        var canonical = _api.GetCanonicalType(type);
        switch ((ClangTypeKind)canonical.Kind)
        {
            case ClangTypeKind.Void:
                return new CTypeMapping("Unit", null, null, null);
            case ClangTypeKind.Int or ClangTypeKind.Long or ClangTypeKind.LongLong or
                ClangTypeKind.Short or ClangTypeKind.CharS or ClangTypeKind.SChar or
                ClangTypeKind.UInt or ClangTypeKind.ULong or ClangTypeKind.ULongLong or
                ClangTypeKind.UShort or ClangTypeKind.UChar or ClangTypeKind.CharU or
                ClangTypeKind.Bool or ClangTypeKind.Enum:
                return new CTypeMapping("Int", null, null, null);
            case ClangTypeKind.Float or ClangTypeKind.Double:
                return new CTypeMapping("Float", null, null, null);
            case ClangTypeKind.Record:
            {
                // 按值结构体（raylib 的 Vector2/Rectangle/Color 等）映射为命名字段记录。
                var spelling = _api.GetString(_api.GetTypeSpelling(canonical));
                var name = RecordNameFromSpelling(spelling);
                if (_records.TryGetValue(name, out var record) && record.Mappable)
                {
                    if (!_resolvingRecords)
                    {
                        _usedRecords.Add(record.EidosName);
                    }

                    return new CTypeMapping(record.EidosName, null, spelling, record.EidosName);
                }

                return null;
            }
            case ClangTypeKind.Pointer:
                return MapPointerType(canonical);
            default:
                return null;
        }
    }

    /// <summary>值记录映射：EidosType 即记录名（指针记录的 EidosType 是 RawPtr）。</summary>
    private static bool IsValueRecord(CTypeMapping mapping) =>
        mapping.RecordName != null && mapping.RecordName == mapping.EidosType;

    /// <summary>记录零值构造（C 缺省初始化语义）；RawPtr 字段置空并登记 Ffi 依赖。</summary>
    private string ZeroValue(string recordName, TranslationState state)
    {
        var record = _records[recordName];
        var parts = record.Fields!.Select(field => $"{field.Name}: {ZeroOf(field.EidosType, state)}");
        return $"{record.EidosName} {{ {string.Join(", ", parts)} }}";
    }

    private string ZeroOf(string eidosType, TranslationState state)
    {
        if (eidosType == "Unit")
        {
            return "()";
        }

        if (_records.TryGetValue(eidosType, out var record) && record.Mappable && IsValueRecord(new CTypeMapping(record.EidosName, null, record.CSpelling, record.EidosName)))
        {
            return ZeroValue(record.EidosName, state);
        }

        if (eidosType == "Float")
        {
            return "0.0";
        }

        if (eidosType == "RawPtr")
        {
            state.NeedsFfiImport = true;
            return "Ffi.null_pointer()";
        }

        return "0";
    }

    private CTypeMapping? MapPointerType(ClangType canonicalPointer)
    {
        var pointee = _api.GetCanonicalType(_api.GetPointeeType(canonicalPointer));
        switch ((ClangTypeKind)pointee.Kind)
        {
            case ClangTypeKind.Int or ClangTypeKind.Long or ClangTypeKind.LongLong or
                ClangTypeKind.Short or ClangTypeKind.CharS or ClangTypeKind.SChar or
                ClangTypeKind.UInt or ClangTypeKind.ULong or ClangTypeKind.ULongLong or
                ClangTypeKind.UShort or ClangTypeKind.UChar or ClangTypeKind.CharU or
                ClangTypeKind.Bool or ClangTypeKind.Enum:
                return new CTypeMapping("RawPtr", "Int", null, null);
            case ClangTypeKind.Float or ClangTypeKind.Double:
                return new CTypeMapping("RawPtr", "Float", null, null);
            case ClangTypeKind.Record:
            {
                var spelling = _api.GetString(_api.GetTypeSpelling(pointee));
                var recordName = RecordNameFromSpelling(spelling);
                if (string.IsNullOrWhiteSpace(recordName))
                {
                    return null;
                }

                // 与值记录表统一命名（typedef/标签差异折叠），保证 accessor 前缀一致。
                if (_records.TryGetValue(recordName, out var record))
                {
                    recordName = record.EidosName;
                }

                return new CTypeMapping("RawPtr", null, spelling, recordName);
            }
            default:
                // void*、多级指针等：可传递比较，不可解引用。
                return new CTypeMapping("RawPtr", null, null, null);
        }
    }

    private List<(ClangTokenKind Kind, string Spelling)> Tokenize(ClangCursor cursor) =>
        _session?.TokenizeCursor(cursor).ToList() ?? [];

    private List<ClangCursor> Children(ClangCursor cursor)
    {
        var children = new List<ClangCursor>();
        _api.VisitChildren(cursor, (child, _, _) =>
        {
            children.Add(child);
            return ClangChildVisitResult.Continue;
        }, IntPtr.Zero);
        return children;
    }

    /// <summary>过滤掉 TypeRef（类型注解）后的值子节点，用于 InitList/复合字面量等混排形态。</summary>
    private List<ClangCursor> ValueChildren(ClangCursor cursor)
    {
        var children = Children(cursor);
        children.RemoveAll(static child => child.Kind == ClangCursorKind2.TypeRef);
        return children;
    }

    /// <summary>判定（可能被隐式转换包裹的）整数字面量。</summary>
    private bool IsIntegerLiteralValue(ClangCursor cursor)
    {
        var current = cursor;
        while (current.Kind == ClangCursorKind2.UnexposedExpr)
        {
            var inner = ValueChildren(current);
            if (inner.Count != 1)
            {
                return false;
            }

            current = inner[0];
        }

        return current.Kind == ClangCursorKind2.IntegerLiteral;
    }

    /// <summary>表达式的 Eidos 类型：解包隐式转换/括号后按底层值判定
    ///（隐式转换节点会报告转换后的 C 类型，而发射文本仍是原值）。</summary>
    private string? EidosTypeOf(ClangCursor expression)
    {
        var current = expression;
        while (current.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr)
        {
            var inner = ValueChildren(current);
            if (inner.Count != 1)
            {
                break;
            }

            current = inner[0];
        }

        return MapType(_api.GetCursorType(current))?.EidosType;
    }

    /// <summary>
    /// C 常规算术转换的窄子集：消费点要求 Float 而值是 Int 时显式转换
    ///（Int→Float 经 runtime eidos_int_to_float；字面量调用方可自行优化）。
    /// </summary>
    private string CoerceNumeric(string text, string? fromType, string toType, TranslationState state)
    {
        if (fromType == toType || fromType != "Int" || toType != "Float")
        {
            return text;
        }

        state.NeedsIntToFloat = true;
        return $"c2e_int_to_float({text})";
    }

    private static string Indent(string text, int width)
    {
        var pad = new string(' ', width);
        return string.Join(Environment.NewLine, text
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => line.Length == 0 ? line : pad + line));
    }

    private sealed class FunctionContext
    {
        public Dictionary<string, CTypeMapping> VarTypes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ParameterNames { get; } = new(StringComparer.Ordinal);
        public string? ReturnEidosType { get; set; }
    }

    private sealed class TranslationState(
        IReadOnlySet<string> definedNames,
        IReadOnlyDictionary<string, ClangCursor> declaredFunctions)
    {
        public IReadOnlySet<string> DefinedNames { get; } = definedNames;
        public IReadOnlyDictionary<string, ClangCursor> DeclaredFunctions { get; } = declaredFunctions;
        public Dictionary<string, IReadOnlyList<CTypeMapping>> FunctionParameters { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PendingExtern> PendingExterns { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Record, string Member), RecordMemberAccess> RecordMembers { get; } = new();
        public Dictionary<string, FloatAbiShim> FloatShims { get; } = new(StringComparer.Ordinal);
        public bool NeedsFfiImport { get; set; }
        public bool NeedsIntToFloat { get; set; }
    }
}

/// <summary>
/// 语句/表达式 cursor kind（clang 22 实测值，与声明段枚举分离维护）。
/// </summary>
internal static class ClangCursorKind2
{
    internal const int TypeRef = 43;
    internal const int UnexposedExpr = 100;
    internal const int DeclRefExpr = 101;
    internal const int MemberRefExpr = 102;
    internal const int CallExpr = 103;
    internal const int IntegerLiteral = 106;
    internal const int FloatingLiteral = 107;
    internal const int StringLiteral = 109;
    internal const int ParenExpr = 111;
    internal const int UnaryOperator = 112;
    internal const int ArraySubscriptExpr = 113;
    internal const int BinaryOperator = 114;
    internal const int CompoundAssignOperator = 115;
    internal const int ConditionalOperator = 116;
    internal const int CStyleCastExpr = 117;
    internal const int CompoundLiteralExpr = 118;
    internal const int InitListExpr = 119;
    internal const int UnexposedStmt = 200;
    internal const int CompoundStmt = 202;
    internal const int IfStmt = 205;
    internal const int WhileStmt = 207;
    internal const int ForStmt = 209;
    internal const int ReturnStmt = 214;
    internal const int DeclStmt = 231;
}
