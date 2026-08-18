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

    /// <summary>成功翻译函数的 Eidos 签名（跨 TU 清单：别的 TU 据此直呼翻译产物）。</summary>
    internal sealed record TranslatedFunctionSignature(
        IReadOnlyList<string> ParameterTypes,
        string ReturnType,
        bool NeedsFfi);

    internal sealed record C2EResult(
        string Source,
        string NativeShimSource,
        IReadOnlyList<string> SkippedFunctions,
        IReadOnlyList<string> FloorSymbols,
        IReadOnlyList<string> CrossTuSymbols,
        IReadOnlyDictionary<string, TranslatedFunctionSignature> FunctionSignatures)
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
        bool MemberIsFloat = false,
        bool NeedsAddress = false);

    /// <summary>
    /// 按值返回结构体的外部函数：sret shim 规格（C 侧静态槽收返回值并给出指针，
    /// Eidos 侧经 accessor 逐字段重组记录）。与 bindgen M4 的按值返回静态槽同型。
    /// </summary>
    private sealed record SretExtern(
        string CName,
        IReadOnlyList<(string Spelling, bool IsFloat)> Parameters,
        string ReturnSpelling,
        bool IsFloor);

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

    /// <summary>TranslateFunctions 的产出：跳过清单、extern 三级分类与翻译成功的签名清单。</summary>
    private sealed record FunctionTranslationOutcome(
        List<string> Skipped,
        IReadOnlyList<string> FloorSymbols,
        IReadOnlyList<string> CrossTuSymbols,
        Dictionary<string, TranslatedFunctionSignature> Signatures);

    /// <summary>声明位于系统头（-isystem 或 clang 内置 SDK 搜索）→ L1 二进制边界，无 C 源可翻。</summary>
    private bool IsSystemDeclaration(ClangCursor cursor) =>
        _api.LocationIsInSystemHeader(_api.GetCursorLocation(cursor)) != 0;

    public C2EResult Translate(string cSourcePath) =>
        Translate(cSourcePath, includePaths: null, defines: null, onlyFunctions: null);

    /// <summary>
    /// 带编译环境（-I/-D/-isystem）的翻译入口：真实项目的 C 源几乎都依赖头搜索路径与配置宏。
    /// crossTuFunctions：其它 TU 已翻译函数的清单——调用它们时直呼翻译产物（同模块），
    /// 不再回退 extern(c) 地板。
    /// </summary>
    public C2EResult Translate(
        string cSourcePath,
        IReadOnlyList<string>? includePaths,
        IReadOnlyList<string>? defines,
        IReadOnlySet<string>? onlyFunctions = null,
        IReadOnlyList<string>? systemIncludePaths = null,
        IReadOnlyDictionary<string, TranslatedFunctionSignature>? crossTuFunctions = null)
    {
        using var session = new ClangSession(_api);
        _session = session;
        _crossTuFunctions = crossTuFunctions;
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
                else if ((ClangCursorKind)cursor.Kind == ClangCursorKind.EnumDecl)
                {
                    foreach (var constant in Children(cursor))
                    {
                        if ((ClangCursorKind)constant.Kind == ClangCursorKind.EnumConstantDecl)
                        {
                            _enumConstants[_api.GetString(_api.GetCursorSpelling(constant))] =
                                _api.GetEnumConstantDeclValue(constant);
                        }
                    }
                }

                return ClangChildVisitResult.Continue;
            }, IntPtr.Zero);

            ResolveRecordFields();
            _resolvingRecords = false;

            var outcome = TranslateFunctions(functions, cSourcePath, out var source, out var shimSource, onlyFunctions, crossTuFunctions);
            return new C2EResult(source, shimSource, outcome.Skipped, outcome.FloorSymbols, outcome.CrossTuSymbols, outcome.Signatures);
        }
        finally
        {
            _session = null;
            _crossTuFunctions = null;
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
    private readonly Dictionary<string, long> _enumConstants = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedEnumConstants = new(StringComparer.Ordinal);

    /// <summary>当前是否在 switch 体翻译中（continue 需映射为退出包装循环的 break）。</summary>
    private int _inSwitchBody;

    /// <summary>其它 TU 已翻译函数清单（项目模式第二遍）：调用直呼翻译产物而非 extern 地板。</summary>
    private IReadOnlyDictionary<string, TranslatedFunctionSignature>? _crossTuFunctions;
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
        // 循环剥离 C 类型前缀词：clang 拼写可能是 "const struct Vector2" 这类多级形态，
        // 只剥一层会把 "struct Vector2" 当记录名（accessor 前缀随之带空格）。
        var name = spelling.Trim();
        while (true)
        {
            var separator = name.IndexOf(' ');
            if (separator <= 0)
            {
                return name;
            }

            if (name[..separator] is "struct" or "union" or "enum" or "const" or "volatile")
            {
                name = name[(separator + 1)..].Trim();
                continue;
            }

            return name;
        }
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

    /// <summary>Eidos 保留字：与 C 标识符同名时字段名加 c2e 后缀（accessor C 符号仍用原名）。</summary>
    private static readonly HashSet<string> ReservedWords =
    [
        "module", "import", "export", "let", "func", "effect", "effects", "type", "trait",
        "fn", "if", "then", "else", "decide", "while", "loop", "match", "when", "return",
        "need", "requires", "break", "continue", "as", "ref", "mut", "mref", "do",
        "unreachable", "quote"
    ];

    private static string SanitizeIdent(string name) =>
        ReservedWords.Contains(name) ? name + "c2e" : name;

    /// <summary>局部/参数的 C 名 → Eidos 名（保留字加 c2e 后缀，声明与引用一致）。</summary>
    private static string EidosRefName(string cName, FunctionContext context) =>
        context.RenamedLocals.TryGetValue(cName, out var renamed) ? renamed : cName;

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
        IReadOnlySet<string>? onlyFunctions = null,
        IReadOnlyDictionary<string, TranslatedFunctionSignature>? crossTuFunctions = null)
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
        // 系统头内带体函数（UCRT 内联数学等）不进候选：留在候选集会让调用走
        // 内部直呼路径发射裸名（其体被跳过不发射，落成未定义符号）。
        var systemHeaderFunctions = new HashSet<string>(
            functions.Where(IsSystemDeclaration).Select(function => _api.GetString(_api.GetCursorSpelling(function))),
            StringComparer.Ordinal);
        _translatableCandidates.UnionWith(
            onlyFunctions ??
            defined.Where(name => name != "main" && !systemHeaderFunctions.Contains(name)));
        var bodies = new List<(string Name, string Text)>();
        var banned = new HashSet<string>(StringComparer.Ordinal);
        var lastSkipReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var signatures = new Dictionary<string, TranslatedFunctionSignature>(StringComparer.Ordinal);
        while (true)
        {
            var bannedCount = banned.Count;
            _bannedCallees.Clear();
            _bannedCallees.UnionWith(banned);
            bodies.Clear();
            skipped.Clear();
            signatures.Clear();

            foreach (var function in functions)
            {
                var name = _api.GetString(_api.GetCursorSpelling(function));
                if (!HasBody(function) || name == "main")
                {
                    continue;
                }

                // 系统头内的带体函数（UCRT 内联数学等）是 L1 二进制边界的组成部分：
                // 翻译无意义，留待 extern 地板路径按需声明。
                if (IsSystemDeclaration(function))
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
                var paramTypes = state.FunctionParameters.TryGetValue(name, out var signatureMappings)
                    ? signatureMappings.Select(static mapping => mapping.EidosType).ToList()
                    : [];
                signatures[name] = new TranslatedFunctionSignature(
                    paramTypes,
                    state.FunctionReturnTypes.GetValueOrDefault(name, "Unit"),
                    state.FunctionUsesFfi.Contains(name));
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
            sb.AppendLine($"    {string.Join($",{Environment.NewLine}    ", record.Fields!.Select(field => $"{SanitizeIdent(field.Name)} :: {field.EidosType}"))}");
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

        // 枚举常量 → 模块级绑定（C 的枚举值是 int 常量，按使用面发射）。
        foreach (var constantName in _usedEnumConstants.OrderBy(static name => name, StringComparer.Ordinal))
        {
            sb.AppendLine($"mut {constantName} := {FormatIntLiteral(_enumConstants[constantName])};");
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
            var memberIsRecord = _records.ContainsKey(access.MemberEidosType);
            if (!memberIsRecord)
            {
                // 标量成员：get/set accessor。struct 成员无安全的按值返回/传参 ABI，
                // 仅经 _addr 链访问（E3051）。
                sb.AppendLine($"@[extern(c, name: \"{prefix}_get\")]");
                sb.AppendLine($"{prefix}_get :: RawPtr -> {access.MemberEidosType} need ffi;");
                sb.AppendLine($"@[extern(c, name: \"{prefix}_set\")]");
                sb.AppendLine($"{prefix}_set :: RawPtr -> {access.MemberEidosType} -> Unit need ffi;");
            }

            if (access.NeedsAddress)
            {
                // &p->m：成员地址（C 侧 offsetof 语义）。
                sb.AppendLine($"@[extern(c, name: \"{prefix}_addr\")]");
                sb.AppendLine($"{prefix}_addr :: RawPtr -> RawPtr need ffi;");
            }

            sb.AppendLine();
        }

        if (state.NeedsFfiImport)
        {
            sb.AppendLine("import std.Ffi");
            sb.AppendLine();
        }

        foreach (var (name, body) in bodies)
        {
            // 体间空行分隔：块级合并（空行分块）依赖它区分相邻函数。
            if (bodies.Count > 0)
            {
                sb.AppendLine();
            }

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
        // 单一 #include：多个 shim 段共用一次包含（input.c 无包含守卫，重复包含即重定义）。
        var shimBodies = new[]
        {
            BuildRecordMemberShimSource(cSourcePath, state.RecordMembers.Values),
            BuildFloatShimSource(cSourcePath, state.FloatShims.Values),
            BuildSretShimSource(cSourcePath, state.SretExterns.Values)
        };
        var shimText = shimBodies.All(static body => body.Length == 0)
            ? string.Empty
            : "// <auto-generated>// Eidos C2E translator shims.// </auto-generated>" + Environment.NewLine +
              $"#include \"{cSourcePath.Replace('\\', '/')}\"" + Environment.NewLine + Environment.NewLine +
              string.Join(Environment.NewLine, shimBodies.Where(static body => body.Length > 0));
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
        return new FunctionTranslationOutcome(skipped, floorSymbols, crossTuSymbols, signatures);
    }

    /// <summary>sret shim：按值返回结构体的外部函数 → 静态槽 + 指针返回；float 参数 double 中转。</summary>
    private string BuildSretShimSource(string cSourcePath, IEnumerable<SretExtern> externs)
    {
        var list = externs.ToList();
        if (list.Count == 0)
        {
            return string.Empty;
        }

        var shim = new StringBuilder();
        foreach (var entry in list)
        {
            var eidosName = $"c2e_ext_{entry.CName}_sret";
            var parameters = string.Join(", ", entry.Parameters.Select(static (parameter, index) =>
                parameter.IsFloat ? $"double p{index}" : $"{parameter.Spelling} p{index}"));
            if (parameters.Length == 0)
            {
                parameters = "void";
            }

            var callArguments = string.Join(", ", entry.Parameters.Select(static (parameter, index) =>
                parameter.IsFloat ? $"(float)p{index}" : $"p{index}"));
            shim.AppendLine($"{entry.ReturnSpelling}* {eidosName}({parameters})");
            shim.AppendLine("{");
            shim.AppendLine($"    static {entry.ReturnSpelling} __slot;");
            shim.AppendLine($"    __slot = {entry.CName}({callArguments});");
            shim.AppendLine("    return &__slot;");
            shim.AppendLine("}");
        }

        return shim.ToString();
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
        foreach (var access in list)
        {
            // 外部链接：Eidos 侧的 extern(c) 引用来自其他编译单元，static 会导致链接期 undefined symbol。
            // C float（32 位）成员以 double 签名中转：Eidos Float extern ABI 是 f64，
            // 直接返回 float 会把 xmm0 高 32 位的未定义位带进 Eidos 侧。
            // struct 成员仅发射 _addr（按值 get/set 无安全 ABI）。
            var prefix = $"c2e_{access.RecordName}_{access.Member}";
            if (_records.ContainsKey(access.MemberEidosType))
            {
                if (access.NeedsAddress)
                {
                    shim.AppendLine($"void* {prefix}_addr(void* __p) {{ return (void*)&((({access.RecordSpelling}*)__p)->{access.Member}); }}");
                }

                continue;
            }

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

            if (access.NeedsAddress)
            {
                shim.AppendLine($"void* {prefix}_addr(void* __p) {{ return (void*)&((({access.RecordSpelling}*)__p)->{access.Member}); }}");
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
            if (SanitizeIdent(paramName) != paramName)
            {
                context.RenamedLocals[paramName] = SanitizeIdent(paramName);
            }
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
        var sretCountBefore = state.SretExterns.Count;
        var bodyText = TranslateFunctionBodyWithParamMutation(Children(body), context, state);
        if (bodyText == null)
        {
            return null;
        }

        if (state.FfiImportTicks > ffiTicksBefore ||
            state.PendingExterns.Count > externCountBefore || state.RecordMembers.Count > memberCountBefore ||
            state.FloatShims.Count > floatShimCountBefore || state.SretExterns.Count > sretCountBefore)
        {
            state.FunctionUsesFfi.Add(name);
        }

        var signature = paramTypes.Count == 0
            ? $"Unit -> {returnType}"
            : $"{string.Join(" -> ", paramTypes)} -> {returnType}";
        state.FunctionReturnTypes[name] = returnType;
        var binders = paramNames.Count == 0
            ? "_ =>"
            : string.Join(" => ", paramNames.Select(name => EidosRefName(name, context))) + " =>";
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

    /// <summary>
    /// 条件位赋值（while/if/do 的 cond 是 (x = f()) 形态）：赋值提升为语句文本，
    /// 条件改用赋值目标（由调用方按 Int 归一）。非该形态时 assignment 为空、条件照常翻译。
    /// </summary>
    private bool TrySplitConditionAssignment(
        ClangCursor conditionCursor,
        FunctionContext context,
        TranslationState state,
        out string? assignment,
        out string? condition)
    {
        assignment = null;
        var current = conditionCursor;
        while (current.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr)
        {
            var inner = ValueChildren(current);
            if (inner.Count != 1)
            {
                break;
            }

            current = inner[0];
        }

        if (current.Kind == ClangCursorKind2.BinaryOperator &&
            _api.GetString(_api.GetCursorSpelling(current)) == "=")
        {
            var operands = Children(current);
            if (operands.Count == 2 && operands[0].Kind == ClangCursorKind2.DeclRefExpr)
            {
                var targetName = _api.GetString(_api.GetCursorSpelling(operands[0]));
                var isMutableLocal = context.VarTypes.ContainsKey(targetName) &&
                    (!context.ParameterNames.Contains(targetName) || context.MutableParams.Contains(targetName));
                if (isMutableLocal &&
                    TranslateAssignment(operands[0], operands[1], context, state) is { } assigned)
                {
                    assignment = $"{assigned};";
                    condition = targetName;
                    return true;
                }
            }
        }

        condition = TranslateExpression(conditionCursor, context, state);
        return condition != null;
    }

    /// <summary>
    /// C 的整数值条件归一为比较（Eidos 条件必须 Bool）。判据按表达式形态而非
    /// clang 类型：C 比较表达式的类型是 int，但翻译产物是 Eidos Bool。
    /// </summary>
    private string? NormalizeConditionText(string? condition, ClangCursor conditionCursor) =>
        condition != null && !IsBoolValuedCondition(conditionCursor) && EidosTypeOf(conditionCursor) == "Int"
            ? $"({condition} != 0)"
            : condition;

    /// <summary>条件表达式是否为 Bool 值形态（比较/逻辑运算及其包裹）。</summary>
    private bool IsBoolValuedCondition(ClangCursor expression)
    {
        var current = expression;
        while (true)
        {
            if (current.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr or ClangCursorKind2.CStyleCastExpr)
            {
                var inner = ValueChildren(current);
                if (inner.Count != 1)
                {
                    return false;
                }

                current = inner[0];
                continue;
            }

            if (current.Kind == ClangCursorKind2.BinaryOperator)
            {
                return _api.GetString(_api.GetCursorSpelling(current)) is "<" or "<=" or ">" or ">=" or "==" or "!=" or "&&" or "||";
            }

            if (current.Kind is ClangCursorKind2.UnaryOperator or ClangCursorKind2.UnaryExpr)
            {
                return Tokenize(current).FirstOrDefault(static token => token.Kind == ClangTokenKind.Punctuation).Spelling == "!";
            }

            return false;
        }
    }

    /// <summary>累积一个 case/default 标签到当前段：空标签的后续标签嵌套为子语句，递归展开。</summary>
    private bool AccumulateSwitchLabel(
        ClangCursor label,
        FunctionContext context,
        TranslationState state,
        List<string> runLabels,
        List<string> allCaseValues,
        List<string> runStatements,
        ref bool runHasDefault)
    {
        var subs = Children(label);
        var skip = 0;
        if (label.Kind == ClangCursorKind2.CaseStmt)
        {
            if (subs.Count < 1)
            {
                SkipReason = "unsupported case label form";
                return false;
            }

            var evaluated = _api.CursorEvaluate(subs[0]);
            if (evaluated == IntPtr.Zero)
            {
                SkipReason = "case label is not a constant integer";
                return false;
            }

            try
            {
                var valueText = FormatIntLiteral(_api.EvalResultGetAsLongLong(evaluated));
                runLabels.Add(valueText);
                allCaseValues.Add(valueText);
            }
            finally
            {
                _api.EvalResultDispose(evaluated);
            }

            skip = 1;
        }
        else
        {
            runHasDefault = true;
        }

        foreach (var sub in subs.Skip(skip))
        {
            if (sub.Kind is ClangCursorKind2.CaseStmt or ClangCursorKind2.DefaultStmt)
            {
                if (!AccumulateSwitchLabel(sub, context, state, runLabels, allCaseValues, runStatements, ref runHasDefault))
                {
                    return false;
                }

                continue;
            }

            var subText = TranslateStatement(sub, context, state);
            if (subText == null)
            {
                return false;
            }

            runStatements.Add(subText);
        }

        return true;
    }

    /// <summary>
    /// switch 去糖：分段（case/default 标签到 break 或 switch 尾为一段）+ 包装循环 + 匹配闩锁。
    /// 标签在段内累积（case 2: case 3: 共享段）；fallthrough 经闩锁链到后续段；
    /// break 终止的段之后闩锁复位；段内条件 break 即 Eidos break（退出包装循环）。
    /// 选择子求值一次（C 语义）。switch 体直接嵌于 case 的循环（Duff 设备类）不支持。
    /// </summary>
    private string? TranslateSwitchStatement(ClangCursor statement, FunctionContext context, TranslationState state)
    {
        var parts = Children(statement);
        if (parts.Count != 2)
        {
            SkipReason = "unsupported switch form";
            return null;
        }

        var selector = TranslateExpression(parts[0], context, state);
        if (selector == null)
        {
            return null;
        }

        if (EidosTypeOf(parts[0]) != "Int")
        {
            SkipReason = "switch selector is not an integer";
            return null;
        }

        var bodyChildren = StatementBodyChildren(parts[1]);
        // 段级结构：run =（段列表, 是否 break 终止）；段 =（标签集, 语句）。
        // 进入某标签只执行该标签所在段及后续段（C 的入口语义），不是整个 run 的全部语句。
        var runs = new List<(List<(List<string> Labels, bool HasDefault, List<string> Statements)> Segments, bool BreakClosed)>();
        var allCaseValues = new List<string>();
        List<string> segLabels = [];
        bool segHasDefault = false;
        var segStatements = new List<string>();
        var segments = new List<(List<string> Labels, bool HasDefault, List<string> Statements)>();
        var runOpen = false;

        void StartSegment()
        {
            segments.Add(([.. segLabels], segHasDefault, [.. segStatements]));
            segLabels = [];
            segHasDefault = false;
            segStatements = [];
        }

        void CloseRun(bool breakClosed)
        {
            if (segLabels.Count > 0 || segHasDefault || segStatements.Count > 0)
            {
                StartSegment();
            }

            if (segments.Count > 0)
            {
                runs.Add(([.. segments], breakClosed));
            }

            segments = [];
            runOpen = false;
        }

        var savedSwitchDepth = _inSwitchBody;
        _inSwitchBody++;
        try
        {
            foreach (var child in bodyChildren)
            {
                if (child.Kind is ClangCursorKind2.CaseStmt or ClangCursorKind2.DefaultStmt)
                {
                    // CaseStmt 名下带首条子语句（children = [值, 语句...]），余下语句是
                    // compound 兄弟节点；空标签的下一标签嵌套为其子语句（case 2: case 3:）。
                    if (segStatements.Count > 0)
                    {
                        StartSegment();
                    }

                    if (!AccumulateSwitchLabel(child, context, state, segLabels, allCaseValues, segStatements, ref segHasDefault))
                    {
                        return null;
                    }

                    runOpen = true;
                }
                else if (child.Kind == ClangCursorKind2.BreakStmt)
                {
                    CloseRun(true);
                }
                else if (runOpen)
                {
                    var translated = TranslateStatement(child, context, state);
                    if (translated == null)
                    {
                        return null;
                    }

                    // 语句只累积；新段仅由后续标签开启（入口点细分）。
                    segStatements.Add(translated);
                }

                // 未进入任何 case 的前导语句：C 语义不可达，丢弃。
            }

            if (runOpen || segLabels.Count > 0 || segHasDefault || segStatements.Count > 0)
            {
                CloseRun(false);
            }
        }
        finally
        {
            _inSwitchBody = savedSwitchDepth;
        }

        var anyCase = string.Join(" || ", allCaseValues.Select(v => $"(c2e_sel == {v})"));
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("    mut c2e_sel := " + selector + ";");
        sb.AppendLine("    loop {");
        sb.AppendLine("        mut c2e_sw_matched := false;");
        foreach (var run in runs)
        {
            for (var segmentIndex = 0; segmentIndex < run.Segments.Count; segmentIndex++)
            {
                var segment = run.Segments[segmentIndex];
                if (segment.Labels.Count == 0 && !segment.HasDefault)
                {
                    continue;
                }

                // 进入本段标签（或 default 且无任何 case 命中）或此前段已命中（fallthrough）。
                var guards = segment.Labels.Select(v => $"(c2e_sel == {v})").ToList();
                string condition;
                if (segment.HasDefault)
                {
                    var defaultGuard = allCaseValues.Count > 0 ? $"!({anyCase})" : "true";
                    condition = guards.Count > 0
                        ? $"c2e_sw_matched || {string.Join(" || ", guards)} || {defaultGuard}"
                        : $"c2e_sw_matched || {defaultGuard}";
                }
                else
                {
                    condition = $"c2e_sw_matched || {string.Join(" || ", guards)}";
                }

                sb.AppendLine($"        if {condition} then {{");
                sb.AppendLine("            c2e_sw_matched := true;");
                foreach (var line in segment.Statements)
                {
                    sb.AppendLine(Indent(line, 12));
                }

                sb.AppendLine("            ()");
                sb.AppendLine("        };");
            }

            if (run.BreakClosed)
            {
                // break 终止的 run 不向后续 run fallthrough：闩锁复位。
                sb.AppendLine("        c2e_sw_matched := false;");
            }
        }

        sb.AppendLine("        break;");
        sb.AppendLine("        ()");
        sb.AppendLine("    };");
        sb.AppendLine("    ()");
        sb.AppendLine("};");
        return sb.ToString();
    }
    /// <summary>
    /// &amp;p->m / &amp;p->a.b 成员地址：指针头路径经 _addr 链推进到末级成员所在记录，
    /// 再取末级成员地址（C 侧 &amp;((R*)p)-&gt;m 语义）。
    /// </summary>
    private string? TranslateMemberAddress(ClangCursor memberAccess, FunctionContext context, TranslationState state)
    {
        if (!TryResolveMemberPath(memberAccess, out var head, out var members) || members.Count == 0)
        {
            return null;
        }

        var headCanonical = _api.GetCanonicalType(_api.GetCursorType(head));
        if ((ClangTypeKind)headCanonical.Kind != ClangTypeKind.Pointer)
        {
            return null;
        }

        var headPointee = _api.GetCanonicalType(_api.GetPointeeType(headCanonical));
        if ((ClangTypeKind)headPointee.Kind != ClangTypeKind.Record)
        {
            return null;
        }

        var chained = ResolvePointerMemberAddressChain(head, members, context, state);
        if (chained == null)
        {
            return null;
        }

        var (address, finalRecord, finalMember) = chained.Value;
        RegisterMemberAccessor(finalRecord, finalMember, state, needsAddress: true);
        return $"c2e_{finalRecord}_{finalMember}_addr({address})";
    }

    /// <summary>
    /// C 允许直接改写参数；Eidos 参数不可变。翻译体遇到参数突变时把该参数登记为可变
    /// 并整体重试——成功后在体首发射影拷贝（mut name := name; 遮蔽不可变参数绑定）。
    /// </summary>
    private string? TranslateFunctionBodyWithParamMutation(List<ClangCursor> body, FunctionContext context, TranslationState state)
    {
        string? bodyText;
        while (true)
        {
            bodyText = TranslateStatements(body, context, state);
            if (bodyText != null)
            {
                break;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                SkipReason ?? string.Empty, @"^mutation of parameter '([^']+)'$");
            if (!match.Success ||
                !context.ParameterNames.Contains(match.Groups[1].Value) ||
                !context.MutableParams.Add(match.Groups[1].Value))
            {
                return null;
            }
        }

        if (context.MutableParams.Count > 0)
        {
            var shadows = string.Join(
                Environment.NewLine,
                context.MutableParams.OrderBy(static p => p, StringComparer.Ordinal)
                    .Select(param => $"mut {EidosRefName(param, context)} := {EidosRefName(param, context)};"));
            bodyText = shadows + Environment.NewLine + bodyText;
        }

        return bodyText;
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

        var eidosVarName = EidosRefName(varName, context);
        var lines = new List<string> { $"mut {eidosVarName} := Ffi.calloc({count})({elementSize});" };
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
                $"Ffi.offset_bytes({eidosVarName})({i} * {elementSize})",
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
            case ClangCursorKind2.SwitchStmt:
                return TranslateSwitchStatement(statement, context, state);

            case ClangCursorKind2.NullStmt:
                return "();";

            case ClangCursorKind2.BreakStmt:
                return "break;";

            case ClangCursorKind2.ContinueStmt:
                // switch 体内的 C continue：退出 switch 包装循环即等价于"跳过 switch 余下
                // 部分"，与落到包装循环尾的 break 相同（外层循环随后照常推进）。
                return _inSwitchBody > 0 ? "break;" : "continue;";

            case ClangCursorKind2.DeclRefExpr:
                // 裸引用语句（多为 (void)x 展开/空宏残留）：无可观测副作用，丢弃。
                return "();";

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
                    if (SanitizeIdent(varName) != varName)
                    {
                        context.RenamedLocals[varName] = SanitizeIdent(varName);
                    }

                    lines.Add($"mut {EidosRefName(varName, context)} := {init};");
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

                if (!TrySplitConditionAssignment(parts[0], context, state, out var ifAssignment, out var condition) ||
                    condition == null)
                {
                    return null;
                }

                condition = NormalizeConditionText(condition, parts[0]);
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

                    var ifResult = $"if {condition} then{Brace(thenBody)} else{Brace(elseBody)};";
                    return ifAssignment == null ? ifResult : $"{ifAssignment}{Environment.NewLine}{ifResult}";
                }

                var ifPlain = $"if {condition} then{Brace(thenBody)} else {{ () }};";
                return ifAssignment == null ? ifPlain : $"{ifAssignment}{Environment.NewLine}{ifPlain}";
            }

            case ClangCursorKind2.WhileStmt:
            {
                var savedSwitchDepth = _inSwitchBody;
                _inSwitchBody = 0;
                var parts = Children(statement);
                if (parts.Count != 2)
                {
                    SkipReason = "unsupported while form";
                    return null;
                }

                if (!TrySplitConditionAssignment(parts[0], context, state, out var condAssignment, out var condition) ||
                    condition == null)
                {
                    _inSwitchBody = savedSwitchDepth;
                    return null;
                }

                condition = NormalizeConditionText(condition, parts[0]);
                var body = TranslateStatements(StatementBodyChildren(parts[1]), context, state);
                if (body == null)
                {
                    _inSwitchBody = savedSwitchDepth;
                    return null;
                }

                var whileSb = new StringBuilder();
                whileSb.AppendLine("loop {");
                if (condAssignment != null)
                {
                    whileSb.AppendLine($"    {condAssignment}");
                }

                whileSb.AppendLine($"    if !({condition}) then break;");
                whileSb.AppendLine(Indent(body, 4));
                whileSb.AppendLine("}");
                var whileResult = $"{whileSb.ToString().TrimEnd()};";
                _inSwitchBody = savedSwitchDepth;
                return whileResult;
            }

            case ClangCursorKind2.DoStmt:
            {
                var savedDoSwitchDepth = _inSwitchBody;
                _inSwitchBody = 0;
                // do-while 去糖：loop { body; if !(cond) then break; }。
                var doParts = Children(statement);
                if (doParts.Count != 2)
                {
                    SkipReason = "unsupported do-while form";
                    return null;
                }

                var doBody = TranslateStatements(StatementBodyChildren(doParts[0]), context, state);
                if (doBody == null)
                {
                    return null;
                }

                if (!TrySplitConditionAssignment(doParts[1], context, state, out var doAssignment, out var doCondition) ||
                    doCondition == null)
                {
                    _inSwitchBody = savedDoSwitchDepth;
                    return null;
                }

                doCondition = NormalizeConditionText(doCondition, doParts[1]);
                var doCondBlock = new StringBuilder();
                doCondBlock.AppendLine("        if !(c2e_do_first) then {");
                if (doAssignment != null)
                {
                    doCondBlock.AppendLine($"            {doAssignment}");
                }

                doCondBlock.AppendLine($"            if !({doCondition}) then break;");
                doCondBlock.AppendLine("            ()");
                doCondBlock.Append("        };");
                // 轮转式去糖（条件提到循环顶 + 首圈旗标）：C 的 continue 语义是
                // 跳到条件判定，直接映射 Eidos continue 落在循环顶即正确。
                var doResult = $"{{{Environment.NewLine}    mut c2e_do_first := true;{Environment.NewLine}    loop {{{Environment.NewLine}{doCondBlock}{Environment.NewLine}        c2e_do_first := false;{Environment.NewLine}{Indent(doBody, 8)}{Environment.NewLine}        (){Environment.NewLine}    }};{Environment.NewLine}    (){Environment.NewLine}}};";
                _inSwitchBody = savedDoSwitchDepth;
                return doResult;
            }

            case ClangCursorKind2.ForStmt:
            {
                var savedForSwitchDepth = _inSwitchBody;
                _inSwitchBody = 0;
                // init / cond / inc / body 去糖为声明 + loop。
                var parts = Children(statement);
                if (parts.Count == 0)
                {
                    SkipReason = "unsupported for form";
                    return null;
                }

                // 空槽（无 init/cond/inc）不发射子节点：DeclStmt/赋值/逗号定性为 init，
                // 自增/复合赋值定性为 inc，其余泛型按序填 cond、inc。
                ClangCursor? initPart = null;
                ClangCursor? conditionPart = null;
                ClangCursor? incrementPart = null;
                var generics = new List<ClangCursor>();
                var seenCondition = false;
                foreach (var part in parts[..^1])
                {
                    var partOp = _api.GetString(_api.GetCursorSpelling(part));
                    if (part.Kind is ClangCursorKind2.CompoundAssignOperator or ClangCursorKind2.UnaryOperator or ClangCursorKind2.UnaryExpr)
                    {
                        // 自增/复合赋值只可能是增量。
                        incrementPart = part;
                    }
                    else if (part.Kind == ClangCursorKind2.DeclStmt)
                    {
                        initPart = part;
                    }
                    else if (part.Kind == ClangCursorKind2.BinaryOperator && partOp is not ("=" or ","))
                    {
                        // 比较类二元（条件）；其余赋值/逗号/调用按位置定 init/inc：
                        // 条件之前为 init（i = 0, j = n），之后为增量（i++, j-- 同为逗号）。
                        seenCondition = true;
                        generics.Add(part);
                    }
                    else if (!seenCondition && initPart == null)
                    {
                        initPart = part;
                    }
                    else
                    {
                        incrementPart = part;
                    }
                }

                if (generics.Count > 2 || (generics.Count == 2 && incrementPart != null))
                {
                    SkipReason = "unsupported for form";
                    return null;
                }

                if (generics.Count > 0)
                {
                    conditionPart = generics[0];
                }

                if (generics.Count > 1)
                {
                    incrementPart = generics[1];
                }

                var init = initPart == null ? string.Empty : TranslateStatement(initPart.Value, context, state);
                if (init == null)
                {
                    return null;
                }

                var condition = conditionPart == null ? null : TranslateExpression(conditionPart.Value, context, state);
                if (condition == null && conditionPart != null)
                {
                    return null;
                }

                var increment = incrementPart == null ? null : TranslateExpression(incrementPart.Value, context, state, asStatement: true);
                if (increment == null && incrementPart != null)
                {
                    return null;
                }

                var body = TranslateStatements(StatementBodyChildren(parts[^1]), context, state);
                if (body == null)
                {
                    return null;
                }

                // C 的 for 是独立作用域（兄弟/嵌套同名循环变量合法）：整个去糖包一层块，
                // 提升到块内的 init 绑定与外层及兄弟声明隔离（否则 E3000 重复绑定）。
                // loop 的闭括号必须带分号：块内后续以 '(' 起头的语句（() 兜底值）会被
                // 解析为对 loop 结果的函数应用（E4000）。
                // 轮转式去糖（增量+条件提到循环顶 + 首圈旗标）：C 的 continue 语义是
                // "跳过循环体余下部分并执行增量后判定"，直接映射 Eidos continue 落在
                // 循环顶（先增量后条件）即正确；C break 语义同为 Eidos break。
                var forSb = new StringBuilder();
                forSb.AppendLine("{");
                if (init.Length > 0)
                {
                    forSb.AppendLine(Indent(init, 4));
                }

                forSb.AppendLine("    mut c2e_inc_first := true;");
                forSb.AppendLine("    loop {");
                forSb.AppendLine("        if !(c2e_inc_first) then {");
                if (increment != null)
                {
                    forSb.AppendLine($"            {increment};");
                }

                if (condition != null)
                {
                    forSb.AppendLine($"            if !({condition}) then break;");
                }

                forSb.AppendLine("            ()");
                forSb.AppendLine("        };");
                forSb.AppendLine("        c2e_inc_first := false;");
                forSb.AppendLine(Indent(body, 8));
                forSb.AppendLine("        ()");
                forSb.AppendLine("    };");
                forSb.AppendLine("    ()");
                forSb.AppendLine("};");
                _inSwitchBody = savedForSwitchDepth;
                return forSb.ToString();
            }

            default:
            {
                // (void)x; 语句：整条丢弃（展开后是裸表达式语句）。
                if (statement.Kind == ClangCursorKind2.CStyleCastExpr &&
                    MapType(_api.GetCursorType(statement))?.EidosType == "Unit")
                {
                    return "();";
                }

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

                // C 截断语义的 Float→Int（(int)d）与指针→整数（(long)p，ptrdiff 算术）。
                if (castMapping.EidosType == "Int" && innerMapping?.EidosType == "Float")
                {
                    state.MarkFfiImport();
                    var floatValue = TranslateExpression(inner[0], context, state);
                    return floatValue == null ? null : $"Ffi.trunc_to_int({floatValue})";
                }

                if (castMapping.EidosType == "Int" && innerMapping?.EidosType == "RawPtr")
                {
                    state.MarkFfiImport();
                    var ptrValue = TranslateExpression(inner[0], context, state);
                    return ptrValue == null ? null : $"Ffi.ptr_as_int({ptrValue})";
                }

                // (void)x 丢弃：求值副作用后归 Unit。
                if (castMapping.EidosType == "Unit")
                {
                    var discarded = TranslateExpression(inner[0], context, state, asStatement: true);
                    return discarded == null
                        ? null
                        : $"{{{Environment.NewLine}    {discarded};{Environment.NewLine}    (){Environment.NewLine}}}";
                }

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
                if (_enumConstants.ContainsKey(name))
                {
                    _usedEnumConstants.Add(name);
                }

                // 不可映射全局（静态数组/查找表等）的引用：诚实跳过该函数，
                // 而非发射裸名落成未定义符号。
                if (!context.VarTypes.ContainsKey(name) &&
                    _globals.TryGetValue(name, out var unsupportedGlobal) &&
                    MapType(_api.GetCursorType(unsupportedGlobal)) == null)
                {
                    SkipReason = $"global '{name}' has an unsupported type";
                    return null;
                }

                return EidosRefName(name, context);
            }

            case ClangCursorKind2.IntegerLiteral:
                return EvaluateLiteral(expression, integer: true);

            case ClangCursorKind2.FloatingLiteral:
                return EvaluateLiteral(expression, integer: false);

            case ClangCursorKind2.StringLiteral:
                return TranslateStringLiteral(expression);

            case ClangCursorKind2.CharacterLiteral:
                // C 字符字面量是 int：clang 常量求值直接给出码点。
                return EvaluateLiteral(expression, integer: true);

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
                        parts.Add($"{SanitizeIdent(field.Name)}: {ZeroOf(field.EidosType, state)}");
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

                    parts.Add($"{SanitizeIdent(field.Name)}: {value}");
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

                if (!IsBoolValuedCondition(operands[0]) && EidosTypeOf(operands[0]) == "Int")
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

                // 逗号序列语句位（for 初始化/增量的多动作形态）：逐段平铺，值为末段。
                if (op == "," && asStatement)
                {
                    var commaLeft = TranslateExpression(operands[0], context, state, asStatement: true);
                    var commaRight = TranslateExpression(operands[1], context, state, asStatement: true);
                    return commaLeft == null || commaRight == null
                        ? null
                        : $"{commaLeft};{Environment.NewLine}{commaRight}";
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

            // &p->m：成员地址经 C 侧 offsetof shim（c2e_<R>_<m>_addr）。
            if (current.Kind == ClangCursorKind2.MemberRefExpr &&
                TranslateMemberAddress(current, context, state) is { } memberAddress)
            {
                return memberAddress;
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
            // 基可以是 cast 表达式（*(int*)p）：按操作数自身类型的 pointee 定元素，
            // 不依赖变量声明类型；可解析局部或显式 cast 之一才放行。
            var operandIsCast = operand.Kind == ClangCursorKind2.CStyleCastExpr;
            if (!operandIsCast && !TryResolveBaseVariable(operand, context, out _))
            {
                SkipReason = "dereference of a pointer without a supported element type";
                return null;
            }

            var pointeeCanonical = _api.GetCanonicalType(
                _api.GetPointeeType(_api.GetCanonicalType(_api.GetCursorType(operand))));
            if ((ClangTypeKind)pointeeCanonical.Kind == ClangTypeKind.Record)
            {
                SkipReason = "dereference of a record pointer outside member access";
                return null;
            }

            var pointer = TranslateExpression(operand, context, state);
            if (pointer == null)
            {
                return null;
            }

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

        // 指针根成员路径（s->f / p->a.b / ((T*)q)->x.y）：struct 中间成员经 _addr shim
        // 逐级推进（extern 返回结构体无桥），末级标量 accessor get。必须先于值记录分支：
        // 链的中间基类型是值记录不代表整条链值根（o->in.a 的基 o->in 是记录，根 o 是指针）。
        if (TryResolveMemberPath(expression, out var pathHead, out var pathMembers) && pathMembers.Count > 0)
        {
            var pathHeadCanonical = _api.GetCanonicalType(_api.GetCursorType(pathHead));
            if ((ClangTypeKind)pathHeadCanonical.Kind == ClangTypeKind.Pointer)
            {
                var pathPointee = _api.GetCanonicalType(_api.GetPointeeType(pathHeadCanonical));
                if ((ClangTypeKind)pathPointee.Kind == ClangTypeKind.Record)
                {
                    return FormatPointerMemberPathRead(pathHead, pathMembers, context, state);
                }
            }
        }

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
            return baseText == null ? null : $"{baseText}.{SanitizeIdent(member)}";
        }

        SkipReason = "member access on a non-record-pointer base";
        return null;
    }

    /// <summary>
    /// 指针头成员路径读取：地址自头出发（变量/cast/下标元素地址），struct 中间成员经
    /// c2e_&lt;R&gt;_&lt;m&gt;_addr 逐级推进，末级标量经 accessor get。中间成员非 struct 或
    /// 末级非标量则跳过。
    /// </summary>
    private string? FormatPointerMemberPathRead(
        ClangCursor head,
        List<string> members,
        FunctionContext context,
        TranslationState state)
    {
        var chained = ResolvePointerMemberAddressChain(head, members, context, state);
        if (chained == null)
        {
            return null;
        }

        var (address, finalRecord, finalMember) = chained.Value;
        RegisterMemberAccessor(finalRecord, finalMember, state);
        return $"c2e_{finalRecord}_{finalMember}_get({address})";
    }

    /// <summary>记录成员 accessor 登记（get/set/可选 addr 的 C shim 与 extern 声明由此发射）。</summary>
    private void RegisterMemberAccessor(string recordName, string member, TranslationState state, bool needsAddress = false)
    {
        var memberClangType = TryGetMemberClangType(recordName, member);
        var memberEidosType = memberClangType == null
            ? TryGetMemberField(recordName, member)?.EidosType ?? "Int"
            : MapType(memberClangType.Value)?.EidosType ?? "Int";
        var spelling = _records.TryGetValue(recordName, out var record) ? record.CSpelling : recordName;
        var cSpelling = memberClangType != null
            ? _api.GetString(_api.GetTypeSpelling(memberClangType.Value))
            : memberEidosType;
        var key = (recordName, member);
        var access = state.RecordMembers.TryGetValue(key, out var existing)
            ? existing
            : new RecordMemberAccess(spelling, recordName, member, cSpelling, memberEidosType,
                memberClangType != null && (ClangTypeKind)memberClangType.Value.Kind == ClangTypeKind.Float);
        if (needsAddress)
        {
            access = access with { NeedsAddress = true };
        }

        state.RecordMembers[key] = access;
    }

    /// <summary>记录字段查找（Eidos 名）。</summary>
    private RecordField? TryGetMemberField(string recordName, string member)
    {
        return _records.TryGetValue(recordName, out var record) && record.Mappable
            ? record.Fields!.FirstOrDefault(f => f.Name == member)
            : null;
    }

    /// <summary>记录字段 clang 类型（float 标记等布局事实）。</summary>
    private ClangType? TryGetMemberClangType(string recordName, string member)
    {
        if (!_records.TryGetValue(recordName, out var record) || record.Declaration is not { } declaration)
        {
            return null;
        }

        foreach (var child in Children(declaration))
        {
            if ((ClangCursorKind)child.Kind == ClangCursorKind.FieldDecl &&
                _api.GetString(_api.GetCursorSpelling(child)) == member)
            {
                return _api.GetCanonicalType(_api.GetCursorType(child));
            }
        }

        return null;
    }

    /// <summary>
    /// 指针头成员路径寻址：返回（末级成员所在地址, 末级记录 Eidos 名, 末级成员名）。
    /// struct 中间成员经 _addr shim 推进；末级必须是标量字段。
    /// </summary>
    private (string Address, string FinalRecord, string FinalMember)? ResolvePointerMemberAddressChain(
        ClangCursor head,
        List<string> members,
        FunctionContext context,
        TranslationState state)
    {
        var headCanonical = _api.GetCanonicalType(_api.GetCursorType(head));
        // 记录元素下标头（pts[i]）的自身类型即记录元素；指针头取 pointee。
        var pointee = head.Kind == ClangCursorKind2.ArraySubscriptExpr &&
            (ClangTypeKind)headCanonical.Kind == ClangTypeKind.Record
                ? headCanonical
                : _api.GetCanonicalType(_api.GetPointeeType(headCanonical));
        var spelling = _api.GetString(_api.GetTypeSpelling(pointee));
        var recordName = RecordNameFromSpelling(spelling);
        if (_records.TryGetValue(recordName, out var headRecord))
        {
            recordName = headRecord.EidosName;
        }

        string? address = head.Kind == ClangCursorKind2.ArraySubscriptExpr
            ? TranslateSubscriptAddress(head, context, state)
            : TranslateExpression(head, context, state);
        if (address == null)
        {
            return null;
        }

        for (var i = 0; i < members.Count - 1; i++)
        {
            var fieldName = TryGetMemberField(recordName, members[i])?.EidosType;
            if (fieldName == null || !_records.ContainsKey(fieldName))
            {
                SkipReason = $"member '{members[i]}' is not a struct field on the path";
                return null;
            }

            RegisterMemberAccessor(recordName, members[i], state, needsAddress: true);
            address = $"c2e_{recordName}_{members[i]}_addr({address})";
            recordName = fieldName;
        }

        var finalMember = members[^1];
        var finalField = TryGetMemberField(recordName, finalMember);
        if (finalField == null || _records.ContainsKey(finalField.EidosType))
        {
            SkipReason = $"member '{finalMember}' is not a scalar field";
            return null;
        }

        return (address, recordName, finalMember);
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

    /// <summary>
    /// 成员路径赋值：s.x = v / a.b.c = v / p->x.y = v / pts[i].x = v。
    /// 头是记录值局部 → 嵌套 record update（h := h.&#123;m1: h.m1.&#123;m2: ...&#125;&#125;）；
    /// 头是指向记录的指针表达式（变量/cast/下标元素地址）→ 首成员经 accessor get/set，
    /// 余下路径在取出的记录值上做嵌套 update。
    /// </summary>
    private string? TranslateMemberPathAssignment(
        ClangCursor target,
        ClangCursor value,
        FunctionContext context,
        TranslationState state)
    {
        if (!TryResolveMemberPath(target, out var head, out var members) || members.Count == 0)
        {
            SkipReason = "unsupported member assignment target";
            return null;
        }

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
        return TranslateMemberPathStoreText(target, assigned, head, members, context, state);
    }

    /// <summary>成员路径写回（值文本已就绪，不再矫正）：复合赋值/自增语句位共用。</summary>
    private string? TranslateMemberPathStoreText(
        ClangCursor target,
        string assigned,
        FunctionContext context,
        TranslationState state)
    {
        if (!TryResolveMemberPath(target, out var head, out var members) || members.Count == 0)
        {
            SkipReason = "unsupported member assignment target";
            return null;
        }

        return TranslateMemberPathStoreText(target, assigned, head, members, context, state);
    }

    private string? TranslateMemberPathStoreText(
        ClangCursor target,
        string assigned,
        ClangCursor head,
        List<string> members,
        FunctionContext context,
        TranslationState state)
    {
        // 头 1：记录值局部（s.x = v）。
        if (head.Kind == ClangCursorKind2.DeclRefExpr)
        {
            var headName = _api.GetString(_api.GetCursorSpelling(head));
            if (context.VarTypes.TryGetValue(headName, out var headMapping) && IsValueRecord(headMapping))
            {
                var headEidosName = EidosRefName(headName, context);
                if (context.ParameterNames.Contains(headName) && !context.MutableParams.Contains(headName))
                {
                    SkipReason = $"mutation of parameter '{headName}'";
                    return null;
                }

                var updated = BuildNestedRecordUpdate(headEidosName, headMapping.EidosType, members, assigned);
                return updated == null ? null : $"{headEidosName} := {updated}";
            }

            if (headName.Length > 0 && _globals.TryGetValue(headName, out var globalDecl) &&
                MapType(_api.GetCursorType(globalDecl)) is { } globalMapping && IsValueRecord(globalMapping))
            {
                MarkGlobalUsed(headName);
                var updatedGlobal = BuildNestedRecordUpdate(SanitizeIdent(headName), globalMapping.EidosType, members, assigned);
                return updatedGlobal == null ? null : $"{SanitizeIdent(headName)} := {updatedGlobal}";
            }
        }

        // 头 2：指向记录的指针表达式（p / (T*)q / 链式基）或记录元素下标（pts[i]，
        // 其自身类型即记录元素而非指针）：struct 中间成员经 _addr shim 推进，末级标量 accessor set。
        var headCanonical = _api.GetCanonicalType(_api.GetCursorType(head));
        var headIsRecordSubscript = head.Kind == ClangCursorKind2.ArraySubscriptExpr &&
            (ClangTypeKind)headCanonical.Kind == ClangTypeKind.Record;
        if ((ClangTypeKind)headCanonical.Kind == ClangTypeKind.Pointer || headIsRecordSubscript)
        {
            var chained = ResolvePointerMemberAddressChain(head, members, context, state);
            if (chained == null)
            {
                return null;
            }

            var (address, finalRecord, finalMember) = chained.Value;
            RegisterMemberAccessor(finalRecord, finalMember, state);
            return $"c2e_{finalRecord}_{finalMember}_set({address})({assigned})";
        }

        SkipReason = "unsupported member assignment target";
        return null;
    }

    /// <summary>成员访问链解析：a.b.c / p->a.b / pts[i].x.y →（头游标, 自外向内成员名列表）。</summary>
    private bool TryResolveMemberPath(ClangCursor memberAccess, out ClangCursor head, out List<string> members)
    {
        members = [];
        head = default;
        var current = memberAccess;
        while (current.Kind == ClangCursorKind2.MemberRefExpr)
        {
            members.Insert(0, _api.GetString(_api.GetCursorSpelling(current)));
            var operands = Children(current);
            if (operands.Count != 1)
            {
                return false;
            }

            var unwrapped = operands[0];
            // 保留 CStyleCast 作头：cast 携带路径根的类型事实（((T*)q)->f 的 T*），
            // 剥掉会退回声明类型（如 void*）。
            while (unwrapped.Kind is ClangCursorKind2.UnexposedExpr or ClangCursorKind2.ParenExpr)
            {
                var inner = ValueChildren(unwrapped);
                if (inner.Count != 1)
                {
                    break;
                }

                unwrapped = inner[0];
            }

            if (unwrapped.Kind == ClangCursorKind2.MemberRefExpr)
            {
                current = unwrapped;
                continue;
            }

            head = unwrapped;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 在记录值文本上沿成员路径构建更新：整记录重建（路径字段取新值，其余字段从
    /// 原值逐字段拷贝）。不用 .{} 简写的嵌套形态——其基必须是稳定绑定，
    /// 投影基（o.in）会被拒（E4000）。
    /// </summary>
    private string? BuildNestedRecordUpdate(string recordValueText, string? recordType, List<string> members, string assigned)
    {
        if (members.Count == 0)
        {
            return assigned;
        }

        if (recordType == null || !_records.TryGetValue(recordType, out var record) || !record.Mappable)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var field in record.Fields!)
        {
            if (field.Name == members[0])
            {
                var inner = BuildNestedRecordUpdate(
                    $"{recordValueText}.{SanitizeIdent(field.Name)}",
                    field.EidosType,
                    members.Count > 1 ? members[1..] : [],
                    assigned);
                if (inner == null)
                {
                    return null;
                }

                parts.Add($"{SanitizeIdent(field.Name)}: {inner}");
            }
            else
            {
                parts.Add($"{SanitizeIdent(field.Name)}: {recordValueText}.{SanitizeIdent(field.Name)}");
            }
        }

        return $"{recordType} {{ {string.Join(", ", parts)} }}";
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
            if (op != "*" || targetOperands.Count != 1)
            {
                SkipReason = "unsupported assignment target dereference";
                return null;
            }

            var targetPointeeKind = (ClangTypeKind)_api.GetCanonicalType(
                _api.GetPointeeType(_api.GetCanonicalType(_api.GetCursorType(targetOperands[0])))).Kind;
            var derefBaseIsCast = targetOperands[0].Kind == ClangCursorKind2.CStyleCastExpr;
            if (targetPointeeKind == ClangTypeKind.Record ||
                (!derefBaseIsCast && !TryResolveBaseVariable(targetOperands[0], context, out _)))
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

        // 成员路径赋值（统一：s.x / a.b.c / p->x.y / pts[i].x）：见 TranslateMemberPathAssignment。
        if (target.Kind == ClangCursorKind2.MemberRefExpr)
        {
            return TranslateMemberPathAssignment(target, value, context, state);
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

        // 成员目标复合赋值/自增（o.in.b += v / p->x++ / pts[i].y *= 2）：
        // 读取-合并后的值文本经成员路径写回（不再矫正）。
        if (target.Kind == ClangCursorKind2.MemberRefExpr)
        {
            return TranslateMemberPathStoreText(target, valueText, context, state);
        }

        if (target.Kind == ClangCursorKind2.DeclRefExpr)
        {
            var name = _api.GetString(_api.GetCursorSpelling(target));
            if (context.VarTypes.TryGetValue(name, out _))
            {
                var eidosName = EidosRefName(name, context);
                if (context.ParameterNames.Contains(name) && !context.MutableParams.Contains(name))
                {
                    SkipReason = $"mutation of parameter '{name}'";
                    return null;
                }

                return $"{eidosName} := {valueText}";
            }

            // 文件级全局 → 模块级 mut 绑定。
            if (_globals.TryGetValue(name, out var declaration) && MapType(_api.GetCursorType(declaration)) != null)
            {
                MarkGlobalUsed(name);
                return $"{SanitizeIdent(name)} := {valueText}";
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

    /// <summary>
    /// extern 按值返回结构体 → sret shim 桥：C 侧静态槽收返回值给指针，Eidos 侧 extern
    /// 返回 RawPtr，调用点经 accessor 逐字段重组为记录值（块表达式内绑定槽一次）。
    /// C float（32 位）参数在 shim 签名侧一并 double 中转。
    /// </summary>
    private string? TranslateSretExternalCall(
        string callee,
        ClangCursor declaration,
        List<ClangCursor> argumentList,
        List<CTypeMapping> parameterMappings,
        CTypeMapping returnMapping,
        FunctionContext context,
        TranslationState state)
    {
        if (!_records.TryGetValue(returnMapping.EidosType, out var record) || !record.Mappable)
        {
            SkipReason = $"extern '{callee}' returns a struct with unmappable fields";
            return null;
        }

        var functionType = _api.GetCursorType(declaration);
        var arity = _api.GetNumArgTypes(functionType);
        var parameterSpells = new List<(string Spelling, bool IsFloat)>(argumentList.Count);
        for (var i = 0; i < arity; i++)
        {
            var rawArg = _api.GetCanonicalType(_api.GetArgType(functionType, (uint)i));
            parameterSpells.Add((
                _api.GetString(_api.GetTypeSpelling(_api.GetArgType(functionType, (uint)i))),
                (ClangTypeKind)rawArg.Kind == ClangTypeKind.Float));
        }

        state.SretExterns.TryAdd(callee, new SretExtern(
            callee,
            parameterSpells,
            _api.GetString(_api.GetTypeSpelling(_api.GetCanonicalType(_api.GetResultType(functionType)))),
            IsSystemDeclaration(declaration)));

        var sretName = $"c2e_ext_{callee}_sret";
        state.PendingExterns.TryAdd(callee, new PendingExtern(
            sretName,
            sretName,
            parameterMappings.Select(static mapping => mapping.EidosType).ToList(),
            "RawPtr",
            sretName,
            IsSystemDeclaration(declaration)));

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

        // 槽在块表达式内绑定一次，各字段经 accessor 读取重组。
        var fields = new List<string>();
        foreach (var field in record.Fields!)
        {
            var fieldClang = TryGetMemberClangType(record.EidosName, field.Name);
            state.RecordMembers[(record.EidosName, field.Name)] = new RecordMemberAccess(
                record.CSpelling,
                record.EidosName,
                field.Name,
                fieldClang != null ? _api.GetString(_api.GetTypeSpelling(fieldClang.Value)) : field.EidosType,
                field.EidosType,
                fieldClang != null && (ClangTypeKind)fieldClang.Value.Kind == ClangTypeKind.Float);
            fields.Add($"{SanitizeIdent(field.Name)}: c2e_{record.EidosName}_{field.Name}_get(slot)");
        }

        return $"{{{Environment.NewLine}    slot := {sretName}({string.Join(", ", argumentTexts)});{Environment.NewLine}    {record.EidosName} {{ {string.Join(", ", fields)} }}{Environment.NewLine}}}";
    }

    private string? TranslateExternalCall(string callee, IEnumerable<ClangCursor> arguments, FunctionContext context, TranslationState state)
    {
        // 跨 TU 清单命中：该函数在其它 TU 已翻译为同模块函数——直呼并按清单签名矫正实参。
        if (_crossTuFunctions != null &&
            state.DeclaredFunctions.TryGetValue(callee, out var manifestDeclaration) &&
            _api.CursorIsVariadic(manifestDeclaration) == 0 &&
            _crossTuFunctions.TryGetValue(callee, out var manifest))
        {
            var manifestFunctionType = _api.GetCursorType(manifestDeclaration);
            var manifestArity = (int)_api.GetNumArgTypes(manifestFunctionType);
            var manifestArguments = arguments.ToList();
            if (manifestArity == manifestArguments.Count && manifestArity == manifest.ParameterTypes.Count)
            {
                var manifestTexts = new List<string>(manifestArguments.Count);
                for (var i = 0; i < manifestArguments.Count; i++)
                {
                    var mapping = MapType(_api.GetArgType(manifestFunctionType, (uint)i));
                    var manifestType = mapping?.EidosType ?? manifest.ParameterTypes[i];
                    var translated = TranslateCallArgument(manifestArguments[i],
                        new CTypeMapping(manifestType, mapping?.ElementEidosType, mapping?.RecordSpelling, mapping?.RecordName),
                        context,
                        state);
                    if (translated == null)
                    {
                        return null;
                    }

                    manifestTexts.Add(translated);
                }

                if (manifest.NeedsFfi)
                {
                    // 调用 need ffi 的翻译函数：调用方签名同样需要。
                    state.FunctionUsesFfi.Add(state.CurrentFunction);
                }

                return $"{callee}({string.Join(", ", manifestTexts)})";
            }
        }

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

        // 按值返回的结构体：经 sret shim（静态槽 + accessor 重组）桥接；
        // 按值传参的结构体仍挡住（Eidos 侧缺少调用点记录→blob 的通用桥）。
        if (IsValueRecord(returnMapping))
        {
            return TranslateSretExternalCall(callee, declaration, argumentList, parameterMappings, returnMapping, context, state);
        }

        if (parameterMappings.Any(static mapping => IsValueRecord(mapping)))
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
                return FormatIntLiteral(_api.EvalResultGetAsLongLong(result));
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

    /// <summary>
    /// 整数字面量文本：Eidos 词法默认按 Int32 解析，超出 i32 值域的 C 无符号常量
    /// （如 2864434397 = 0xAABBCCDD）必须带 Int64 后缀 l。
    /// </summary>
    private static string FormatIntLiteral(long value) =>
        value is > int.MaxValue or < int.MinValue ? $"{value}l" : value.ToString();

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
        var parts = record.Fields!.Select(field => $"{SanitizeIdent(field.Name)}: {ZeroOf(field.EidosType, state)}");
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
            case ClangTypeKind.ConstantArray or ClangTypeKind.IncompleteArray:
                // 多维数组（T a[N][M]）按平坦缓冲承载：内层下标经数组退化语义复合寻址。
                return new CTypeMapping("RawPtr", null, null, null);
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

        /// <summary>被 C 直接改写的参数：体首影拷贝为 mut 局部（遮蔽不可变参数绑定）。</summary>
        public HashSet<string> MutableParams { get; } = new(StringComparer.Ordinal);

        /// <summary>C 名 → Eidos 名（保留字标识符重命名表）。</summary>
        public Dictionary<string, string> RenamedLocals { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TranslationState(
        IReadOnlySet<string> definedNames,
        IReadOnlyDictionary<string, ClangCursor> declaredFunctions)
    {
        public IReadOnlySet<string> DefinedNames { get; } = definedNames;
        public IReadOnlyDictionary<string, ClangCursor> DeclaredFunctions { get; } = declaredFunctions;
        public Dictionary<string, IReadOnlyList<CTypeMapping>> FunctionParameters { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> FunctionReturnTypes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PendingExtern> PendingExterns { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Record, string Member), RecordMemberAccess> RecordMembers { get; } = new();
        public Dictionary<string, FloatAbiShim> FloatShims { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SretExtern> SretExterns { get; } = new(StringComparer.Ordinal);
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
    internal const int CharacterLiteral = 110;
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
    internal const int NullStmt = 230;
    internal const int UnexposedStmt = 200;
    internal const int CompoundStmt = 202;
    internal const int IfStmt = 205;
    internal const int CaseStmt = 203;
    internal const int DefaultStmt = 204;
    internal const int SwitchStmt = 206;
    internal const int ContinueStmt = 212;
    internal const int BreakStmt = 213;
    internal const int WhileStmt = 207;
    internal const int DoStmt = 208;
    internal const int ForStmt = 209;
    internal const int ReturnStmt = 214;
    internal const int DeclStmt = 231;
}
