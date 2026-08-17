using System.Text;

namespace Eidosc.Bindgen.Clang;

/// <summary>
/// C2E 源到源翻译（M7 切片 + 特性矩阵扩展）：把 C 函数体翻译为 Eidos 函数。
/// 支持子集：int/float 标量算术与比较、一元 -/!/~/取址（&amp;a[i]、&amp;数组局部）、局部变量
/// （声明/赋值；T a[N] 局部映射 RawPtr 堆缓冲）、if/else、while/for（经 loop+break 去糖）、
/// return、同文件已翻译函数的调用、指针（T* 映射 RawPtr：经 std.Ffi 的
/// load/store/pointer_eq/null_pointer/offset_bytes）、数组下标与指针算术寻址
/// （a[i]/p[i][j]：元素地址 = offset_bytes(base)(i * sizeof(T))；C float 元素经 c2e_f32 shim）、
/// union/struct 指针的成员访问与记录元素下标成员（经自动生成的 c2e_* C shim 与 extern(c) 声明）、
/// sizeof 常量求值、对外部 C 函数的调用（自动生成 extern(c) 声明，C 侧由调用方链接）。
/// C 定宽整数溢出不建模（统一提升为 Eidos Int/Float，值域内语义一致）；
/// 其余构造（局部取址 &amp;x、switch/goto/三目、复合赋值表达式值位、表达式位自增自减、
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
        IReadOnlyList<string> SkippedFunctions,
        IReadOnlyList<string> FloorSymbols,
        IReadOnlyList<string> CrossTuSymbols)
    {
        public bool IsEmpty => Source.Length == 0;
    }

    /// <summary>被翻译函数引用的 union/struct 成员访问（生成 accessor shim 与 extern 声明）。
    /// <see cref="MemberIsFloat"/> 标记 C float（32 位）成员：shim 以 double 签名中转，
    /// 与 Eidos Float（f64）extern ABI 对齐。</summary>
    private sealed record RecordMemberAccess(
        string RecordSpelling,
        string RecordName,
        string Member,
        string MemberCType,
        string MemberEidosType,
        bool MemberIsFloat = false);

    /// <summary>含 C float（32 位）的外部函数：double 中转 shim 规格。</summary>
    private sealed record FloatAbiShim(
        string CName,
        IReadOnlyList<(string Spelling, bool IsFloat)> Parameters,
        (string Spelling, bool IsFloat) Return);

    /// <summary>
    /// 被翻译函数调用的外部 C 函数。<see cref="ForeignCName"/> 是真正的外来链接符号
    /// （float ABI shim 生效时 <see cref="CName"/> 指向自建 shim）；<see cref="IsFloor"/>
    /// 标注 L1 地板（声明在系统头 = 二进制边界），否则为跨 TU 的项目符号。
    /// </summary>
    private sealed record PendingExtern(
        string CName,
        string EidosName,
        IReadOnlyList<string> ParameterTypes,
        string ReturnType,
        string ForeignCName,
        bool IsFloor);

    /// <summary>TranslateFunctions 的产出：跳过清单与 extern 三级分类结果。</summary>
    private sealed record FunctionTranslationOutcome(
        List<string> Skipped,
        IReadOnlyList<string> FloorSymbols,
        IReadOnlyList<string> CrossTuSymbols);

    /// <summary>声明位于系统头（-isystem 或 clang 内置 SDK 搜索）→ L1 二进制边界，无 C 源可翻。</summary>
    private bool IsSystemDeclaration(ClangCursor cursor) =>
        _api.LocationIsInSystemHeader(_api.GetCursorLocation(cursor)) != 0;

    public C2EResult Translate(string cSourcePath) =>
        Translate(cSourcePath, includePaths: null, defines: null, onlyFunctions: null);

    /// <summary>
    /// 带编译环境（-I/-D/-isystem）的翻译入口：真实项目的 C 源几乎都依赖头搜索路径与配置宏。
    /// </summary>
    public C2EResult Translate(
        string cSourcePath,
        IReadOnlyList<string>? includePaths,
        IReadOnlyList<string>? defines,
        IReadOnlySet<string>? onlyFunctions = null,
        IReadOnlyList<string>? systemIncludePaths = null)
    {
        using var session = new ClangSession(_api);
        _session = session;
        try
        {
            session.Parse(cSourcePath, includePaths: includePaths, defines: defines, skipFunctionBodies: false, systemIncludePaths: systemIncludePaths);
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

            var outcome = TranslateFunctions(functions, cSourcePath, out var source, out var shimSource, onlyFunctions);
            return new C2EResult(source, shimSource, outcome.Skipped, outcome.FloorSymbols, outcome.CrossTuSymbols);
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

    private FunctionTranslationOutcome TranslateFunctions(
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
        var bodies = new List<(string Name, string Text)>();
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
                    bodies.Add((name, $"// SKIP {name}: {lastSkipReasons.GetValueOrDefault(name, "untranslated")}"));
                    bodies.Add((name, string.Empty));
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
                    bodies.Add((name, $"// SKIP {name}: {SkipReason}"));
                    bodies.Add((name, string.Empty));
                    continue;
                }

                bodies.Add((name, translated));
            }

            if (banned.Count == bannedCount)
            {
                break;
            }
        }

        // need ffi 沿内部调用图传播（调用"需要 need ffi 的被调函数"的调用方同样需要）。
        while (true)
        {
            var propagated = false;
            foreach (var (caller, callees) in state.FunctionCallees)
            {
                if (state.FunctionUsesFfi.Contains(caller) ||
                    !callees.Any(state.FunctionUsesFfi.Contains))
                {
                    continue;
                }

                state.FunctionUsesFfi.Add(caller);
                propagated = true;
            }

            if (!propagated)
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

        foreach (var (name, body) in bodies)
        {
            if (body.Length > 0 && state.FunctionUsesFfi.Contains(name))
            {
                // 签名行补 need ffi（首行即 "{name} :: {signature}"）。
                var newline = body.IndexOf('\n');
                sb.AppendLine(newline < 0
                    ? body + " need ffi"
                    : body[..newline] + " need ffi" + body[newline..]);
            }
            else
            {
                sb.AppendLine(body);
            }
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

        // extern 三级分类：系统头声明 = L1 地板符号（链接契约），项目头/主文件声明 = 跨 TU 符号。
        // 注：不动点重试期间可能留下未被最终引用的 extern（上游被禁后其引用消失），清单为保守过近似。
        var floorSymbols = state.PendingExterns.Values
            .Where(static pending => pending.IsFloor)
            .Select(static pending => pending.ForeignCName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();
        var crossTuSymbols = state.PendingExterns.Values
            .Where(static pending => !pending.IsFloor)
            .Select(static pending => pending.ForeignCName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();
        return new FunctionTranslationOutcome(skipped, floorSymbols, crossTuSymbols);
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
            // C float（32 位）成员以 double 签名中转：Eidos Float extern ABI 是 f64，
            // 直接返回 float 会把 xmm0 高 32 位的未定义位带进 Eidos 侧。
            var prefix = $"c2e_{access.RecordName}_{access.Member}";
            if (access.MemberIsFloat)
            {
                shim.AppendLine($"double {prefix}_get(void* __p) {{ return (double)((({access.RecordSpelling}*)__p)->{access.Member}); }}");
                shim.AppendLine($"void {prefix}_set(void* __p, double __v) {{ (({access.RecordSpelling}*)__p)->{access.Member} = ({access.MemberCType})__v; }}");
            }
            else
            {
                shim.AppendLine($"{access.MemberCType} {prefix}_get(void* __p) {{ return (({access.RecordSpelling}*)__p)->{access.Member}; }}");
                shim.AppendLine($"void {prefix}_set(void* __p, {access.MemberCType} __v) {{ (({access.RecordSpelling}*)__p)->{access.Member} = __v; }}");
            }
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
        var name = _api.GetString(_api.GetCursorSpelling(function));
        state.CurrentFunction = name;

        // 效果事实快照：体内直接触及 Ffi intrinsic / extern / accessor / f32 shim 的函数
        // 签名必须带 need ffi（效果推断不覆盖循环体/嵌套操作数位的调用）。
        var ffiTicksBefore = state.FfiImportTicks;
        var externCountBefore = state.PendingExterns.Count;
        var memberCountBefore = state.RecordMembers.Count;
        var floatShimCountBefore = state.FloatShims.Count;
        var bodyText = TranslateStatements(Children(body), context, state);
        if (bodyText == null)
        {
            return null;
        }

        if (state.FfiImportTicks > ffiTicksBefore ||
            state.PendingExterns.Count > externCountBefore || state.RecordMembers.Count > memberCountBefore ||
            state.FloatShims.Count > floatShimCountBefore)
        {
            state.FunctionUsesFfi.Add(name);
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

    /// <summary>
    /// T a[N] 局部：堆缓冲承载（RawPtr）。无初始化器 → malloc（C 未初始化语义，读前必写）；
    /// 带初始化列表 → calloc + 逐元素 store（C 缺省元素零填充语义）。
    /// 记录/嵌套数组元素仅支持无初始化器形态（成员经下标基 accessor 访问）；
    /// char 数组的字符串字面量初始化本轮不支持。
    /// </summary>
    private List<string>? TranslateLocalArray(
        ClangCursor varDecl,
        ClangType canonicalArrayType,
        List<ClangCursor> initChildren,
        FunctionContext context,
        TranslationState state)
    {
        var varName = _api.GetString(_api.GetCursorSpelling(varDecl));
        var elementCanonical = _api.GetCanonicalType(_api.GetArrayElementType(canonicalArrayType));
        var elementSize = _api.TypeGetSizeOf(elementCanonical);
        var count = _api.GetArraySize(canonicalArrayType);
        if (elementSize <= 0 || count <= 0)
        {
            SkipReason = $"local array '{varName}' has an unknown element size or length";
            return null;
        }

        state.MarkFfiImport();

        // 声明子节点：数组尺寸表达式在 clang 中作为 VarDecl 的子节点出现（常量尺寸即 IntegerLiteral）；
        // 初始化器（如有）是最后一个值子节点，解包隐式转换后判定形态。
        var initializer = default(ClangCursor?);
        if (initChildren.Count > 0)
        {
            var last = initChildren[^1];
            while (last.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr)
            {
                var inner = ValueChildren(last);
                if (inner.Count != 1)
                {
                    break;
                }

                last = inner[0];
            }

            if (last.Kind is ClangCursorKind2.InitListExpr or ClangCursorKind2.StringLiteral)
            {
                initializer = last;
            }
        }

        if (initializer is null)
        {
            return [$"mut {varName} := Ffi.malloc({count} * {elementSize});"];
        }

        if (initializer.Value.Kind == ClangCursorKind2.StringLiteral)
        {
            SkipReason = $"local array '{varName}' has a string literal initializer";
            return null;
        }

        var elementKind = (ClangTypeKind)elementCanonical.Kind;
        if (elementKind is ClangTypeKind.Record or ClangTypeKind.ConstantArray or ClangTypeKind.IncompleteArray)
        {
            SkipReason = $"local array '{varName}' has an unsupported initializer element type";
            return null;
        }

        var elements = ValueChildren(initializer.Value);
        if (elements.Count > count)
        {
            SkipReason = $"local array '{varName}' initializer has more elements than its length";
            return null;
        }

        var lines = new List<string> { $"mut {varName} := Ffi.calloc({count})({elementSize});" };
        var elementType = MapType(elementCanonical)?.EidosType;
        for (var i = 0; i < elements.Count; i++)
        {
            var value = TranslateExpression(elements[i], context, state);
            if (value == null)
            {
                return null;
            }

            value = CoerceNumeric(value, EidosTypeOf(elements[i]), elementType, state);
            var store = FormatElementStore(
                elementCanonical,
                $"Ffi.offset_bytes({varName})({i} * {elementSize})",
                value,
                state);
            if (store == null)
            {
                return null;
            }

            lines.Add($"{store};");
        }

        return lines;
    }

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
                    var declarationType = _api.GetCursorType(varDecl);
                    var canonicalDeclType = _api.GetCanonicalType(declarationType);
                    var isArrayLocal = (ClangTypeKind)canonicalDeclType.Kind
                        is ClangTypeKind.ConstantArray or ClangTypeKind.IncompleteArray;
                    var varType = MapType(declarationType, allowArrays: isArrayLocal);
                    if (varType == null)
                    {
                        SkipReason = $"local '{varName}' has unsupported type";
                        return null;
                    }

                    // 记录类型的 VarDecl 首个子节点是 TypeRef（类型注解），过滤后再取初始化表达式。
                    var initChildren = ValueChildren(varDecl);
                    if (isArrayLocal)
                    {
                        var arrayLines = TranslateLocalArray(varDecl, canonicalDeclType, initChildren, context, state);
                        if (arrayLines == null)
                        {
                            return null;
                        }

                        context.VarTypes[varName] = varType;
                        lines.AddRange(arrayLines);
                        continue;
                    }

                    string? init;
                    if (initChildren.Count > 0)
                    {
                        init = TranslateExpression(initChildren[0], context, state);
                        if (init != null)
                        {
                            init = CoerceNumeric(init, EidosTypeOf(initChildren[0]), varType.EidosType, state);
                            init = CoerceStringToPointerTarget(initChildren[0], init, varType.EidosType, state);
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
                value = CoerceStringToPointerTarget(valueChildren[0], value, context.ReturnEidosType, state);
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

                // C 的 for 是独立作用域（兄弟/嵌套同名循环变量合法）：整个去糖包一层块，
                // 提升到块内的 init 绑定与外层及兄弟声明隔离（否则 E3000 重复绑定）。
                // loop 的闭括号必须带分号：块内后续以 '(' 起头的语句（() 兜底值）会被
                // 解析为对 loop 结果的函数应用（E4000）。
                var forSb = new StringBuilder();
                forSb.AppendLine("{");
                forSb.AppendLine(Indent(init, 4));
                forSb.AppendLine("    loop {");
                forSb.AppendLine($"        if !({condition}) then break;");
                forSb.AppendLine(Indent(body, 8));
                forSb.AppendLine($"        {increment};");
                forSb.AppendLine("        ()");
                forSb.AppendLine("    };");
                forSb.AppendLine("    ()");
                forSb.AppendLine("};");
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

                // 复合赋值语句（x += v / a[i] += v）去糖为读取-合并-写回。
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

                // 自增/自减语句（x++ / ++x / a[i]++）经表达式位去糖路径
                //（读取-加一-写回，TryFormatStorageAssignment 覆盖标量与下标目标）。
                // 裸表达式语句（赋值/调用）。
                if (statement.Kind is ClangCursorKind2.BinaryOperator or ClangCursorKind2.CallExpr or
                    ClangCursorKind2.UnaryOperator or ClangCursorKind2.UnaryExpr)
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

            case ClangCursorKind2.StringLiteral:
                return TranslateStringLiteral(expression);

            case ClangCursorKind2.UnaryOperator or ClangCursorKind2.UnaryExpr:
                // 新版 libclang 把 sizeof/alignof 等一元内置表达式从 UnaryOperator 拆为 UnaryExpr（136）。
                return TranslateUnaryOperator(expression, context, state, asStatement);

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
                    // 位运算（Int-only）：Eidos 侧同形运算符透传；两侧强制 Int 语义由 C 源保证。
                    "&" or "|" or "^" or "<<" or ">>" => $"{left} {op} {right}",
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

            case ClangCursorKind2.ArraySubscriptExpr:
                return TranslateSubscriptValue(expression, context, state);

            default:
            {
                SkipReason = expression.Kind switch
                {
                    ClangCursorKind2.ConditionalOperator => "ternary conditional",
                    _ => $"unsupported expression kind {expression.Kind}"
                };
                return null;
            }
        }
    }

    /// <summary>下标元素地址：Ffi.offset_bytes(base)(index * sizeof(element))；clang 布局事实定 sizeof。</summary>
    private string? TranslateSubscriptAddress(ClangCursor subscript, FunctionContext context, TranslationState state)
    {
        var operands = Children(subscript);
        if (operands.Count != 2)
        {
            SkipReason = "unsupported subscript form";
            return null;
        }

        var elementCanonical = _api.GetCanonicalType(_api.GetCursorType(subscript));
        var elementSize = _api.TypeGetSizeOf(elementCanonical);
        if (elementSize <= 0)
        {
            SkipReason = "subscript element type has an unknown size";
            return null;
        }

        var baseText = TranslateExpression(operands[0], context, state);
        var indexText = TranslateExpression(operands[1], context, state);
        if (baseText == null || indexText == null)
        {
            return null;
        }

        state.MarkFfiImport();
        return $"Ffi.offset_bytes({baseText})(({indexText}) * {elementSize})";
    }

    /// <summary>下标读取：按元素 clang 类型分派 load；数组元素（多维内层）按 C 退化语义返回地址本身。</summary>
    private string? TranslateSubscriptValue(ClangCursor subscript, FunctionContext context, TranslationState state)
    {
        var address = TranslateSubscriptAddress(subscript, context, state);
        return address == null
            ? null
            : FormatElementLoad(_api.GetCanonicalType(_api.GetCursorType(subscript)), address, state);
    }

    /// <summary>
    /// 按元素 clang 类型生成读取文本。C 定宽标量按 clang 布局宽度选择 intrinsic：
    /// Ffi.load[Int]/store[Int] 是 i64 存取，C int/short/char/float 必须用对应宽度变体，
    /// 否则相邻元素被跨写/读到合并值。数组元素（多维内层）按 C 退化语义返回地址本身。
    /// </summary>
    private string? FormatElementLoad(ClangType elementCanonical, string address, TranslationState state)
    {
        state.MarkFfiImport();
        switch ((ClangTypeKind)elementCanonical.Kind)
        {
            case ClangTypeKind.Int or ClangTypeKind.Long or ClangTypeKind.LongLong or
                ClangTypeKind.Short or ClangTypeKind.CharS or ClangTypeKind.SChar or
                ClangTypeKind.UInt or ClangTypeKind.ULong or ClangTypeKind.ULongLong or
                ClangTypeKind.UShort or ClangTypeKind.UChar or ClangTypeKind.CharU or
                ClangTypeKind.Bool or ClangTypeKind.Enum:
            {
                var load = TypedLoadText(_api.TypeGetSizeOf(elementCanonical), address);
                if (load == null)
                {
                    SkipReason = "subscript element type has an unsupported size";
                    return null;
                }

                return load;
            }

            case ClangTypeKind.Float:
                return $"Ffi.load_f32({address})";
            case ClangTypeKind.Double:
                return $"Ffi.load[Float]({address})";
            case ClangTypeKind.Pointer:
                return $"Ffi.load[RawPtr]({address})";
            case ClangTypeKind.ConstantArray or ClangTypeKind.IncompleteArray:
                // m[i]（元素本身是数组）退化为元素首地址，供外层下标/指针语境使用。
                return address;
            case ClangTypeKind.Record:
                SkipReason = "subscript of a record element outside member access";
                return null;
            default:
                SkipReason = "subscript element type is not a supported scalar or pointer";
                return null;
        }
    }

    /// <summary>按元素 clang 类型生成写入文本（a[i] = v 的右端已就绪）。</summary>
    private string? FormatElementStore(ClangType elementCanonical, string address, string value, TranslationState state)
    {
        state.MarkFfiImport();
        switch ((ClangTypeKind)elementCanonical.Kind)
        {
            case ClangTypeKind.Int or ClangTypeKind.Long or ClangTypeKind.LongLong or
                ClangTypeKind.Short or ClangTypeKind.CharS or ClangTypeKind.SChar or
                ClangTypeKind.UInt or ClangTypeKind.ULong or ClangTypeKind.ULongLong or
                ClangTypeKind.UShort or ClangTypeKind.UChar or ClangTypeKind.CharU or
                ClangTypeKind.Bool or ClangTypeKind.Enum:
            {
                var store = TypedStoreText(_api.TypeGetSizeOf(elementCanonical), address, value);
                if (store == null)
                {
                    SkipReason = "assignment to an unsupported array element size";
                    return null;
                }

                return store;
            }

            case ClangTypeKind.Float:
                return $"Ffi.store_f32({address})({value})";
            case ClangTypeKind.Double:
                return $"Ffi.store[Float]({address})({value})";
            case ClangTypeKind.Pointer:
                return $"Ffi.store[RawPtr]({address})({value})";
            default:
                SkipReason = "assignment to an unsupported array element type";
                return null;
        }
    }

    /// <summary>C 整数宽度 → load intrinsic 文本（Eidos Int 是 i64，C int/short/char 用窄变体）。</summary>
    private string? TypedLoadText(long byteSize, string address) => byteSize switch
    {
        8 => $"Ffi.load[Int]({address})",
        4 => $"Ffi.load_i32({address})",
        2 => $"Ffi.load_i16({address})",
        1 => $"Ffi.load_i8({address})",
        _ => null
    };

    /// <summary>C 整数宽度 → store intrinsic 文本。</summary>
    private string? TypedStoreText(long byteSize, string address, string value) => byteSize switch
    {
        8 => $"Ffi.store[Int]({address})({value})",
        4 => $"Ffi.store_i32({address})({value})",
        2 => $"Ffi.store_i16({address})({value})",
        1 => $"Ffi.store_i8({address})({value})",
        _ => null
    };

    private string? TranslateUnaryOperator(
        ClangCursor expression,
        FunctionContext context,
        TranslationState state,
        bool asStatement = false)
    {
        var tokens = Tokenize(expression);
        var op = tokens.FirstOrDefault(static token => token.Kind == ClangTokenKind.Punctuation).Spelling;
        var operands = Children(expression);

        // sizeof(x)/sizeof(T)：整型常量表达式，优先 clang 求值，失败回退操作数类型布局尺寸。
        // 先于操作数数量检查：sizeof(T) 的 UnaryOperator 没有表达式子节点。
        if (tokens.FirstOrDefault(static token => token.Kind == ClangTokenKind.Keyword).Spelling == "sizeof")
        {
            var size = EvaluateSizeof(expression, operands.Count > 0 ? operands[0] : expression);
            if (size > 0)
            {
                return size.ToString();
            }

            SkipReason = "sizeof of an unsupported operand";
            return null;
        }

        // 语句位自增/自减（含 for 增量位、a[i]++ 后缀形态）去糖为读取-加一-写回；
        // 值位语义（返回旧值）不支持。token 扫描不取位置：后缀 a[i]++ 的首个标点是 '['。
        if (tokens.FirstOrDefault(static token =>
                token.Kind == ClangTokenKind.Punctuation && token.Spelling is "++" or "--").Spelling
            is { } incDec)
        {
            if (!asStatement || operands.Count != 1)
            {
                SkipReason = "increment/decrement in expression context";
                return null;
            }

            var current = TranslateExpression(operands[0], context, state);
            if (current == null)
            {
                return null;
            }

            var arithmetic = incDec == "++" ? $"{current} + 1" : $"{current} - 1";
            return TryFormatStorageAssignment(operands[0], arithmetic, context, state);
        }

        if (op == null || operands.Count != 1)
        {
            SkipReason = "unsupported unary operator";
            return null;
        }

        var operand = operands[0];
        if (op == "&")
        {
            // &a[i] → 元素地址；&arr（局部数组）→ 数组首地址（同为 RawPtr 值）。
            var current = operand;
            while (current.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr or ClangCursorKind2.CStyleCastExpr)
            {
                var inner = ValueChildren(current);
                if (inner.Count != 1)
                {
                    break;
                }

                current = inner[0];
            }

            if (current.Kind == ClangCursorKind2.ArraySubscriptExpr)
            {
                return TranslateSubscriptAddress(current, context, state);
            }

            if (current.Kind == ClangCursorKind2.DeclRefExpr &&
                (ClangTypeKind)_api.GetCanonicalType(_api.GetCursorType(current)).Kind
                    is ClangTypeKind.ConstantArray or ClangTypeKind.IncompleteArray)
            {
                return _api.GetString(_api.GetCursorSpelling(current));
            }

            SkipReason = "unsupported address-of operand";
            return null;
        }

        if (op == "*")
        {
            if (!TryResolveBaseVariable(operand, context, out var varType) || varType.ElementEidosType == null)
            {
                SkipReason = "dereference of a pointer without a supported element type";
                return null;
            }

            var pointer = TranslateExpression(operand, context, state);
            if (pointer == null)
            {
                return null;
            }

            // 解引用同样按 pointee 的 clang 布局宽度选择 intrinsic（Ffi.load[Int] 是 i64 存取）。
            var pointeeCanonical = _api.GetCanonicalType(
                _api.GetPointeeType(_api.GetCanonicalType(_api.GetCursorType(operand))));
            return FormatElementLoad(pointeeCanonical, pointer, state);
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
                state.MarkFfiImport();
                return $"Ffi.pointer_eq({operandText})(Ffi.null_pointer())";
            }

            return $"({operandText} == 0)";
        }

        if (op == "-")
        {
            var operandText = TranslateExpression(operand, context, state);
            return operandText == null ? null : $"-{operandText}";
        }

        // ~x → (x ^ -1)：按位取反 = 与全 1 异或（补码恒等），Eidos 无需专门的一元运算符。
        if (op == "~")
        {
            var operandText = TranslateExpression(operand, context, state);
            return operandText == null ? null : $"({operandText} ^ -1)";
        }

        SkipReason = $"unsupported unary operator '{op}'";
        return null;
    }

    /// <summary>sizeof 求值：clang 常量求值优先（覆盖 sizeof(type) 与 VLA 之外的一切 ICE），
    /// 失败回退到操作数（解包后）的 clang 布局尺寸。</summary>
    private long EvaluateSizeof(ClangCursor sizeofExpression, ClangCursor operand)
    {
        var result = _api.CursorEvaluate(sizeofExpression);
        if (result != IntPtr.Zero)
        {
            try
            {
                var value = _api.EvalResultGetAsLongLong(result);
                if (value > 0)
                {
                    return value;
                }
            }
            finally
            {
                _api.EvalResultDispose(result);
            }
        }

        var current = operand;
        while (current.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr)
        {
            var inner = ValueChildren(current);
            if (inner.Count != 1)
            {
                break;
            }

            current = inner[0];
        }

        return _api.TypeGetSizeOf(_api.GetCanonicalType(_api.GetCursorType(current)));
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

        // p[i].f（基是记录元素下标）：元素地址上的 accessor get。
        if (TryResolveSubscriptMemberTarget(expression, out var subscriptBase, out var elementRecord) &&
            TranslateSubscriptAddress(subscriptBase, context, state) is { } memberAddress)
        {
            var subscriptMember = _api.GetString(_api.GetCursorSpelling(expression));
            var subscriptMemberMapping = MapType(_api.GetCursorType(expression));
            if (subscriptMemberMapping == null)
            {
                SkipReason = $"member '{subscriptMember}' has an unsupported type";
                return null;
            }

            state.RecordMembers[(elementRecord.RecordName!, subscriptMember)] = new RecordMemberAccess(
                elementRecord.RecordSpelling!,
                elementRecord.RecordName!,
                subscriptMember,
                _api.GetString(_api.GetTypeSpelling(_api.GetCursorType(expression))),
                subscriptMemberMapping.EidosType,
                (ClangTypeKind)_api.GetCanonicalType(_api.GetCursorType(expression)).Kind == ClangTypeKind.Float);
            return $"c2e_{elementRecord.RecordName}_{subscriptMember}_get({memberAddress})";
        }

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
            memberMapping2.EidosType,
            (ClangTypeKind)_api.GetCanonicalType(_api.GetCursorType(expression)).Kind == ClangTypeKind.Float);
        state.RecordMembers[(varType.RecordName!, member)] = access;
        return $"c2e_{varType.RecordName}_{member}_get({pointer})";
    }

    /// <summary>
    /// 成员访问的基是记录元素下标（p[i].f，含隐式转换/括号包裹）：解包后给出
    /// 下标游标与元素记录映射。仅记录元素；指针基（p->f）与值记录基不由本路径处理。
    /// </summary>
    private bool TryResolveSubscriptMemberTarget(
        ClangCursor memberAccess,
        out ClangCursor subscriptBase,
        out CTypeMapping elementRecord)
    {
        subscriptBase = default;
        elementRecord = null!;
        var operands = Children(memberAccess);
        if (operands.Count != 1)
        {
            return false;
        }

        var current = operands[0];
        while (current.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr or ClangCursorKind2.CStyleCastExpr)
        {
            var inner = ValueChildren(current);
            if (inner.Count != 1)
            {
                return false;
            }

            current = inner[0];
        }

        if (current.Kind != ClangCursorKind2.ArraySubscriptExpr)
        {
            return false;
        }

        // 下标表达式的类型即元素类型；仅记录元素有成员语义。
        var elementCanonical = _api.GetCanonicalType(_api.GetCursorType(current));
        if ((ClangTypeKind)elementCanonical.Kind != ClangTypeKind.Record)
        {
            return false;
        }

        var spelling = _api.GetString(_api.GetTypeSpelling(elementCanonical));
        var recordName = RecordNameFromSpelling(spelling);
        if (_records.TryGetValue(recordName, out var record))
        {
            recordName = record.EidosName;
        }

        subscriptBase = current;
        elementRecord = new CTypeMapping("RawPtr", null, spelling, recordName);
        return true;
    }

    /// <summary>成员写入的 accessor set 文本：登记 RecordMemberAccess（含 C float 成员标记）。</summary>
    private string? FormatMemberAccessorStore(
        CTypeMapping recordMapping,
        ClangCursor memberAccess,
        string address,
        string value,
        TranslationState state)
    {
        var member = _api.GetString(_api.GetCursorSpelling(memberAccess));
        var memberMapping = MapType(_api.GetCursorType(memberAccess));
        if (memberMapping == null)
        {
            SkipReason = $"member '{member}' has an unsupported type";
            return null;
        }

        state.RecordMembers[(recordMapping.RecordName!, member)] = new RecordMemberAccess(
            recordMapping.RecordSpelling!,
            recordMapping.RecordName!,
            member,
            _api.GetString(_api.GetTypeSpelling(_api.GetCursorType(memberAccess))),
            memberMapping.EidosType,
            (ClangTypeKind)_api.GetCanonicalType(_api.GetCursorType(memberAccess)).Kind == ClangTypeKind.Float);
        return $"c2e_{recordMapping.RecordName}_{member}_set({address})({value})";
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

            // 按被指类型的 clang 布局宽度写回（Ffi.store[Int] 是 i64 存取）。
            var targetPointee = _api.GetCanonicalType(
                _api.GetPointeeType(_api.GetCanonicalType(_api.GetCursorType(targetOperands[0]))));
            return FormatElementStore(targetPointee, pointer, assigned, state);
        }

        // a[i] = v → 元素地址 store（复合赋值/自增语句位共用同一写回）。
        if (target.Kind == ClangCursorKind2.ArraySubscriptExpr)
        {
            var address = TranslateSubscriptAddress(target, context, state);
            var assigned = TranslateExpression(value, context, state);
            if (address == null || assigned == null)
            {
                return null;
            }

            var elementCanonical = _api.GetCanonicalType(_api.GetCursorType(target));
            assigned = CoerceNumeric(assigned, EidosTypeOf(value), MapType(elementCanonical)?.EidosType, state);
            return FormatElementStore(elementCanonical, address, assigned, state);
        }

        // p[i].f = v → 元素地址上的 accessor set。
        if (target.Kind == ClangCursorKind2.MemberRefExpr &&
            TryResolveSubscriptMemberTarget(target, out var subscriptBase, out var elementRecord) &&
            TranslateSubscriptAddress(subscriptBase, context, state) is { } elementAddress)
        {
            var member = _api.GetString(_api.GetCursorSpelling(target));
            var assigned = TranslateExpression(value, context, state);
            if (assigned == null)
            {
                return null;
            }

            assigned = CoerceNumeric(
                assigned,
                EidosTypeOf(value),
                MapType(_api.GetCursorType(target))?.EidosType,
                state);
            return FormatMemberAccessorStore(elementRecord, target, elementAddress, assigned, state);
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
            return FormatMemberAccessorStore(varType, target, pointer, assigned2, state);
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
                valueText = CoerceStringToPointerTarget(value, valueText, targetMapping.EidosType, state);
            }
        }

        return TryFormatStorageAssignment(target, valueText, context, state);
    }

    /// <summary>
    /// C 字符串字面量进入 RawPtr 语境（指针参数/局部/返回/赋值目标）时，
    /// 以 Eidos String 承载、在边界处经 Ffi.to_c_string 转为 C 字符串。
    /// 字面量可能被 UnexposedExpr（隐式转换，如 char[N] → const char* 退化）或
    /// 括号/显式转换包裹，先解包再判定。
    /// </summary>
    private string? CoerceStringToPointerTarget(ClangCursor valueCursor, string? valueText, string? targetEidosType, TranslationState state)
    {
        if (valueText == null || targetEidosType != "RawPtr")
        {
            return valueText;
        }

        var current = valueCursor;
        while (current.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr or ClangCursorKind2.CStyleCastExpr)
        {
            var inner = ValueChildren(current);
            if (inner.Count != 1)
            {
                break;
            }

            current = inner[0];
        }

        if (current.Kind != ClangCursorKind2.StringLiteral)
        {
            return valueText;
        }

        state.MarkFfiImport();
        return $"Ffi.to_c_string({valueText})";
    }

    /// <summary>把赋值格式化为 Eidos 可存储目标：mut 局部/模块级全局直接重绑；参数不可变，整体跳过。</summary>
    private string? TryFormatStorageAssignment(ClangCursor target, string valueText, FunctionContext context, TranslationState state)
    {
        // a[i] += v / a[i]++ 语句位：读取-合并后的值写回元素地址（值文本已就绪，不再矫正）。
        if (target.Kind == ClangCursorKind2.ArraySubscriptExpr)
        {
            var address = TranslateSubscriptAddress(target, context, state);
            return address == null
                ? null
                : FormatElementStore(_api.GetCanonicalType(_api.GetCursorType(target)), address, valueText, state);
        }

        // pts[i].x += v / p->x += v 语句位：成员读取-合并后经 accessor 写回。
        if (target.Kind == ClangCursorKind2.MemberRefExpr)
        {
            if (TryResolveSubscriptMemberTarget(target, out var memberSubscript, out var memberElementRecord) &&
                TranslateSubscriptAddress(memberSubscript, context, state) is { } memberAddress)
            {
                return FormatMemberAccessorStore(memberElementRecord, target, memberAddress, valueText, state);
            }

            var targetOperands = Children(target);
            if (targetOperands.Count == 1 &&
                TryResolveBaseVariable(targetOperands[0], context, out var pointerVar) &&
                pointerVar.RecordName != null &&
                TranslateExpression(targetOperands[0], context, state) is { } pointerText)
            {
                return FormatMemberAccessorStore(pointerVar, target, pointerText, valueText, state);
            }
        }

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
                    state.MarkFfiImport();
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

        state.MarkFfiImport();
        var equality = $"Ffi.pointer_eq({leftText})({rightText})";
        return op == "!=" ? $"!({equality})" : equality;
    }

    /// <summary>占位映射：调用实参强转查询失败时按普通标量处理。</summary>
    private static readonly CTypeMapping UnsupportedMapping = new("Int", null, null, null);

    private string? TranslateCall(string callee, IEnumerable<ClangCursor> arguments, FunctionContext context, TranslationState state)
    {
        // 内部调用边登记：被调函数的 need ffi 经调用图传播到调用方。
        if (!string.IsNullOrEmpty(state.CurrentFunction))
        {
            var callees = state.FunctionCallees.TryGetValue(state.CurrentFunction, out var set)
                ? set
                : state.FunctionCallees[state.CurrentFunction] = [];
            callees.Add(callee);
        }

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
                    state.MarkFfiImport();
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

        // 字符串字面量落在 RawPtr 参数位（const char* 形参）：边界处转 C 字符串。
        translated = CoerceStringToPointerTarget(argument, translated, parameter?.EidosType, state);
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
            // 分类：声明在系统头 → L1 地板（无 C 源可翻）；否则为跨 TU 项目符号（供人工核对并入翻译）。
            pending = new PendingExtern(
                floatShimNeeded ? $"c2e_ext_{callee}_f" : callee,
                floatShimNeeded ? $"c2e_ext_{callee}_f" : $"c2e_ext_{callee}",
                parameterMappings.Select(static mapping => mapping.EidosType).ToList(),
                returnMapping.EidosType,
                callee,
                IsSystemDeclaration(declaration));
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

    /// <summary>
    /// C 字符串字面量 → Eidos String 字面量：解码 C 转义（含 \xNN 与八进制），
    /// 以 Eidos 支持的转义集（\\n \\r \\t \\\\ \\0 等见 StringLiteralRule.EscapeMap）重编码；
    /// 该集无法表示的控制字符视为不可翻译。
    /// </summary>
    private string? TranslateStringLiteral(ClangCursor expression)
    {
        var spelling = _api.GetString(_api.GetCursorSpelling(expression));
        var start = spelling.IndexOf('"');
        var end = spelling.LastIndexOf('"');
        if (start < 0 || end <= start)
        {
            SkipReason = "unsupported string literal form";
            return null;
        }

        // 解码（编码前缀 u8/u/U/L 已随起始引号定位剥离）。
        var decoded = new StringBuilder();
        for (var i = start + 1; i < end; i++)
        {
            var c = spelling[i];
            if (c != '\\')
            {
                decoded.Append(c);
                continue;
            }

            if (++i >= end)
            {
                SkipReason = "malformed escape in string literal";
                return null;
            }

            var escape = spelling[i];
            switch (escape)
            {
                case 'n': decoded.Append('\n'); break;
                case 't': decoded.Append('\t'); break;
                case 'r': decoded.Append('\r'); break;
                case 'a': decoded.Append('\a'); break;
                case 'b': decoded.Append('\b'); break;
                case 'v': decoded.Append('\v'); break;
                case 'f': decoded.Append('\f'); break;
                case '0': decoded.Append('\0'); break;
                case '\\': decoded.Append('\\'); break;
                case '"': decoded.Append('"'); break;
                case '\'': decoded.Append('\''); break;
                case 'x':
                {
                    var hex = 0;
                    var digits = 0;
                    while (i + 1 < end && Uri.IsHexDigit(spelling[i + 1]) && digits < 2)
                    {
                        hex = hex * 16 + Uri.FromHex(spelling[++i]);
                        digits++;
                    }

                    decoded.Append((char)hex);
                    break;
                }
                default:
                    // clang 对 StringLiteral 的 spelling 是"值重编码"（如 \x41 已按值、
                    // 仅重转义引号等）：未知转义视为值里真实的 反斜杠+字符。
                    decoded.Append('\\');
                    decoded.Append(escape);
                    break;
            }
        }

        // 重编码到 Eidos 转义集。
        var encoded = new StringBuilder("\"");
        foreach (var ch in decoded.ToString())
        {
            switch (ch)
            {
                case '\\': encoded.Append("\\\\"); break;
                case '"': encoded.Append("\\\""); break;
                case '\n': encoded.Append("\\n"); break;
                case '\t': encoded.Append("\\t"); break;
                case '\r': encoded.Append("\\r"); break;
                case '\a': encoded.Append("\\a"); break;
                case '\b': encoded.Append("\\b"); break;
                case '\v': encoded.Append("\\v"); break;
                case '\f': encoded.Append("\\f"); break;
                case '\0': encoded.Append("\\0"); break;
                default:
                    if (ch < 0x20)
                    {
                        SkipReason = "string literal contains a control character without an Eidos escape";
                        return null;
                    }

                    encoded.Append(ch);
                    break;
            }
        }

        encoded.Append('"');
        return encoded.ToString();
    }

    private static string DefaultZero(string eidosType) => eidosType switch
    {
        "Float" => "0.0",
        "RawPtr" => "Ffi.null_pointer()",
        _ => "0"
    };

    /// <summary>标量与指针的 Eidos 类型映射；指针额外携带元素类型 / 记录（union、struct）事实。</summary>
    private sealed record CTypeMapping(string EidosType, string? ElementEidosType, string? RecordSpelling, string? RecordName);

    private CTypeMapping? MapType(ClangType type, bool allowArrays = false)
    {
        var canonical = _api.GetCanonicalType(type);
        switch ((ClangTypeKind)canonical.Kind)
        {
            case ClangTypeKind.ConstantArray or ClangTypeKind.IncompleteArray
                when allowArrays:
                // C 数组缓冲（局部 T a[N]）：值即首地址，按元素指针映射携带元素事实。
                // 记录字段与全局仍拒绝数组（零初始化布局与 Eidos 记录值不对应）。
                return MapArrayType(canonical);
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
            state.MarkFfiImport();
            return "Ffi.null_pointer()";
        }

        return "0";
    }

    /// <summary>C 数组类型的指针式映射（仅限局部缓冲）：元素事实与 MapPointerType 的 pointee 分类一致。</summary>
    private CTypeMapping? MapArrayType(ClangType canonicalArray)
    {
        var element = _api.GetCanonicalType(_api.GetArrayElementType(canonicalArray));
        switch ((ClangTypeKind)element.Kind)
        {
            case ClangTypeKind.Int or ClangTypeKind.Long or ClangTypeKind.LongLong or
                ClangTypeKind.Short or ClangTypeKind.CharS or ClangTypeKind.SChar or
                ClangTypeKind.UInt or ClangTypeKind.ULong or ClangTypeKind.ULongLong or
                ClangTypeKind.UShort or ClangTypeKind.UChar or ClangTypeKind.CharU or
                ClangTypeKind.Bool or ClangTypeKind.Enum:
                return new CTypeMapping("RawPtr", "Int", null, null);
            case ClangTypeKind.Float or ClangTypeKind.Double:
                return new CTypeMapping("RawPtr", "Float", null, null);
            case ClangTypeKind.Pointer:
                // 元素本身是指针（int* a[N]）：下标读取得到 RawPtr（Ffi.load[RawPtr]）。
                return new CTypeMapping("RawPtr", "RawPtr", null, null);
            case ClangTypeKind.Record:
            {
                var spelling = _api.GetString(_api.GetTypeSpelling(element));
                var recordName = RecordNameFromSpelling(spelling);
                if (string.IsNullOrWhiteSpace(recordName))
                {
                    return null;
                }

                if (_records.TryGetValue(recordName, out var record))
                {
                    recordName = record.EidosName;
                }

                return new CTypeMapping("RawPtr", null, spelling, recordName);
            }
            default:
                return null;
        }
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
        // c2e_int_to_float 是 need ffi 的 extern：调用方签名同样需要 need ffi。
        state.MarkFfiImport();
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
        public bool NeedsFfiImport { get; private set; }
        public bool NeedsIntToFloat { get; set; }

        // Mark 计数（而非仅置位）供按函数快照：bool 是文件级粘性的，
        // 只有首次触碰会改变，后续函数无法据 bool 差异判定自身使用。
        public int FfiImportTicks { get; private set; }

        public void MarkFfiImport()
        {
            NeedsFfiImport = true;
            FfiImportTicks++;
        }

        /// <summary>当前正在翻译的函数名（内部调用边登记用）。</summary>
        public string CurrentFunction { get; set; } = string.Empty;

        /// <summary>函数 → 体内直接使用 Ffi/extern（需要 need ffi 签名）。</summary>
        public HashSet<string> FunctionUsesFfi { get; } = new(StringComparer.Ordinal);

        /// <summary>函数 → 同文件被调函数（need ffi 沿调用图传播）。</summary>
        public Dictionary<string, HashSet<string>> FunctionCallees { get; } = new(StringComparer.Ordinal);
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
    internal const int UnaryExpr = 136;
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
