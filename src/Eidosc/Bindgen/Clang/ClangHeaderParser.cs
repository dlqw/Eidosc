using System.Runtime.InteropServices;

namespace Eidosc.Bindgen.Clang;

/// <summary>
/// clang 提取结果。Errors 非空时 <see cref="Ir"/> 为 null（不产出半成品）。
/// </summary>
internal sealed record ClangHeaderParseResult(
    CHeaderIr? Ir,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 基于 in-process libclang 的全量声明提取器（P0 M2）。
/// 输出与 <see cref="SimpleCHeaderParser"/> 同构的 <see cref="CHeaderIr"/>，
/// 但覆盖 include 展开、typedef 链、union、函数指针、变参、宏常量、全局变量与布局事实。
/// </summary>
internal sealed class ClangHeaderParser
{
    private readonly ClangApi _api;

    internal ClangHeaderParser(ClangApi api)
    {
        _api = api;
    }

    internal ClangHeaderParseResult Parse(
        string headerPath,
        IReadOnlyList<string>? includePaths = null,
        IReadOnlyList<string>? defines = null,
        IReadOnlyList<string>? clangArgs = null)
    {
        using var session = new ClangSession(_api);
        session.Parse(headerPath, includePaths, defines, skipFunctionBodies: false, extraArgs: clangArgs);

        var errors = session.Diagnostics
            .Where(static d => d.StartsWith("Error", StringComparison.Ordinal) || d.StartsWith("Fatal", StringComparison.Ordinal))
            .ToArray();
        var warnings = session.Diagnostics
            .Where(static d => d.StartsWith("Warning", StringComparison.Ordinal) || d.StartsWith("Note", StringComparison.Ordinal))
            .ToArray();
        if (errors.Length > 0)
            return new ClangHeaderParseResult(null, errors, warnings);

        return new ClangHeaderParseResult(Extract(session, headerPath), errors, warnings);
    }

    private CHeaderIr Extract(ClangSession session, string headerPath)
    {
        var functions = new List<CBindingFunction>();
        var structs = new List<CBindingStruct>();
        var unions = new List<CBindingUnion>();
        var enums = new List<CBindingEnum>();
        var typedefs = new List<CBindingTypedef>();
        var constants = new List<CBindingConstant>();
        var globals = new List<CBindingGlobal>();

        session.VisitChildren(session.RootCursor, (cursor, _, _) =>
        {
            if (!IsFromHeader(session, cursor, headerPath))
                return ClangChildVisitResult.Continue;

            switch ((ClangCursorKind)cursor.Kind)
            {
                case ClangCursorKind.FunctionDecl:
                    functions.Add(ExtractFunction(session, cursor));
                    break;
                case ClangCursorKind.StructDecl:
                    structs.Add(ExtractStruct(session, cursor));
                    break;
                case ClangCursorKind.UnionDecl:
                    unions.Add(ExtractUnion(session, cursor));
                    break;
                case ClangCursorKind.EnumDecl:
                    enums.Add(ExtractEnum(session, cursor));
                    break;
                case ClangCursorKind.TypedefDecl:
                    typedefs.Add(ExtractTypedef(session, cursor));
                    break;
                case ClangCursorKind.VarDecl:
                    globals.Add(ExtractGlobal(session, cursor));
                    break;
                case ClangCursorKind.MacroDefinition:
                    ExtractConstant(session, cursor, constants);
                    break;
            }

            return ClangChildVisitResult.Continue;
        }, IntPtr.Zero);

        return new CHeaderIr(headerPath, functions, structs, enums, unions, typedefs, constants, globals);
    }

    private CBindingFunction ExtractFunction(ClangSession session, ClangCursor cursor)
    {
        var name = session.GetCursorSpelling(cursor);
        var returnType = ConvertType(session, _api.GetCursorResultType(cursor));
        var parameters = new List<CBindingParameter>();
        var index = 0;
        session.VisitChildren(cursor, (paramCursor, _, _) =>
        {
            if ((ClangCursorKind)paramCursor.Kind != ClangCursorKind.ParmDecl)
                return ClangChildVisitResult.Continue;

            var paramName = session.GetCursorSpelling(paramCursor);
            var paramType = ConvertType(session, _api.GetCursorType(paramCursor));
            parameters.Add(new CBindingParameter(
                string.IsNullOrEmpty(paramName) ? $"arg{index}" : paramName,
                paramType));
            index++;
            return ClangChildVisitResult.Continue;
        }, IntPtr.Zero);

        return new CBindingFunction(
            name,
            returnType,
            parameters,
            IsVariadic: _api.CursorIsVariadic(cursor) != 0,
            IsInline: _api.CursorIsFunctionInlined(cursor) != 0);
    }

    private CBindingStruct ExtractStruct(ClangSession session, ClangCursor cursor)
    {
        var type = _api.GetCursorType(cursor);
        return new CBindingStruct(
            session.GetCursorSpelling(cursor),
            ExtractFields(session, cursor),
            (int)_api.TypeGetSizeOf(type),
            (int)_api.TypeGetAlignOf(type));
    }

    private CBindingUnion ExtractUnion(ClangSession session, ClangCursor cursor)
    {
        var type = _api.GetCursorType(cursor);
        return new CBindingUnion(
            session.GetCursorSpelling(cursor),
            ExtractFields(session, cursor),
            (int)_api.TypeGetSizeOf(type),
            (int)_api.TypeGetAlignOf(type));
    }

    private List<CBindingField> ExtractFields(ClangSession session, ClangCursor recordCursor)
    {
        var fields = new List<CBindingField>();
        session.VisitChildren(recordCursor, (fieldCursor, _, _) =>
        {
            if ((ClangCursorKind)fieldCursor.Kind != ClangCursorKind.FieldDecl)
                return ClangChildVisitResult.Continue;

            var fieldType = _api.GetCursorType(fieldCursor);
            fields.Add(new CBindingField(
                session.GetCursorSpelling(fieldCursor),
                ConvertType(session, fieldType),
                (int)(_api.CursorGetOffsetOfField(fieldCursor) / 8),
                (int)_api.TypeGetSizeOf(fieldType)));
            return ClangChildVisitResult.Continue;
        }, IntPtr.Zero);
        return fields;
    }

    private CBindingEnum ExtractEnum(ClangSession session, ClangCursor cursor)
    {
        var values = new List<CBindingEnumValue>();
        session.VisitChildren(cursor, (valueCursor, _, _) =>
        {
            if ((ClangCursorKind)valueCursor.Kind != ClangCursorKind.EnumConstantDecl)
                return ClangChildVisitResult.Continue;

            values.Add(new CBindingEnumValue(
                session.GetCursorSpelling(valueCursor),
                _api.GetEnumConstantDeclValue(valueCursor)));
            return ClangChildVisitResult.Continue;
        }, IntPtr.Zero);
        return new CBindingEnum(session.GetCursorSpelling(cursor), values);
    }

    private CBindingTypedef ExtractTypedef(ClangSession session, ClangCursor cursor)
    {
        var underlying = _api.GetTypedefDeclUnderlyingType(cursor);
        var converted = ConvertType(session, underlying);
        return new CBindingTypedef(
            session.GetCursorSpelling(cursor),
            converted.Spelling,
            converted.Kind,
            converted);
    }

    private CBindingGlobal ExtractGlobal(ClangSession session, ClangCursor cursor) =>
        new(session.GetCursorSpelling(cursor), ConvertType(session, _api.GetCursorType(cursor)));

    private void ExtractConstant(ClangSession session, ClangCursor cursor, List<CBindingConstant> constants)
    {
        var name = session.GetCursorSpelling(cursor);
        if (string.IsNullOrEmpty(name))
            return;

        // 路径 1：clang_Cursor_Evaluate（部分版本支持对宏游标求值）。
        var eval = _api.CursorEvaluate(cursor);
        if (eval != IntPtr.Zero)
        {
            try
            {
                switch ((ClangEvalResultKind)_api.EvalResultGetKind(eval))
                {
                    case ClangEvalResultKind.Int:
                        constants.Add(new CBindingConstant(
                            name,
                            _api.EvalResultGetAsLongLong(eval).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            IsString: false));
                        return;
                    case ClangEvalResultKind.StrLiteral:
                    case ClangEvalResultKind.ObjCStrLiteral:
                    case ClangEvalResultKind.CFStr:
                        var strPointer = _api.EvalResultGetAsStr(eval);
                        if (strPointer != IntPtr.Zero)
                        {
                            constants.Add(new CBindingConstant(name, Marshal.PtrToStringUTF8(strPointer) ?? string.Empty, IsString: true));
                            return;
                        }

                        break;
                }
            }
            finally
            {
                _api.EvalResultDispose(eval);
            }
        }

        // 路径 2：extent 分词回退。只接受"恰好一个 Literal token"的对象式宏
        // （整数或字符串字面量）；表达式宏、函数式宏、token paste 等在 M5 收编时
        // 记录 unsupported 清单，不静默产出错误常量。
        var tokens = session.TokenizeCursor(cursor);
        var literalTokens = tokens.Where(static t => t.Kind == ClangTokenKind.Literal).ToList();
        if (literalTokens.Count != 1)
            return;

        var literal = literalTokens[0].Spelling;
        if (literal.Length >= 2 && literal[0] == '"' && literal[^1] == '"')
        {
            constants.Add(new CBindingConstant(name, UnescapeCStringLiteral(literal), IsString: true));
        }
        else if (long.TryParse(
                     literal,
                     System.Globalization.NumberStyles.Integer,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out var value))
        {
            constants.Add(new CBindingConstant(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), IsString: false));
        }
    }

    private static string UnescapeCStringLiteral(string literal)
    {
        // 最小转义：去掉首尾引号并展开常见 C 转义；其余原样保留。
        var body = literal[1..^1];
        var builder = new System.Text.StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i + 1 >= body.Length)
            {
                builder.Append(body[i]);
                continue;
            }

            var next = body[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                '"' => '"',
                '0' => '\0',
                _ => next
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// 顶层声明的类型 → <see cref="CBindingType"/>。Elaborated/Attributed 先规范化；
    /// typedef 保留其名（链式解析交给生成器，经 TypedefsSafe 列表查底层）。
    /// </summary>
    private CBindingType ConvertType(ClangSession session, ClangType type)
    {
        var normalized = NormalizeType(type);
        var kind = (ClangTypeKind)normalized.Kind;
        var spelling = session.GetTypeSpelling(normalized);
        var isConst = _api.IsConstQualifiedType(normalized) != 0;

        switch (kind)
        {
            case ClangTypeKind.Void:
                return new CBindingType(CBindingTypeKind.Void, "void", spelling);

            case ClangTypeKind.Bool:
                return new CBindingType(CBindingTypeKind.Primitive, "bool", spelling);

            case ClangTypeKind.CharU:
            case ClangTypeKind.UChar:
            case ClangTypeKind.CharS:
            case ClangTypeKind.SChar:
            case ClangTypeKind.Short:
            case ClangTypeKind.Int:
            case ClangTypeKind.Long:
            case ClangTypeKind.LongLong:
            case ClangTypeKind.UShort:
            case ClangTypeKind.UInt:
            case ClangTypeKind.ULong:
            case ClangTypeKind.ULongLong:
            case ClangTypeKind.Float:
            case ClangTypeKind.Double:
            {
                var name = kind switch
                {
                    ClangTypeKind.CharU or ClangTypeKind.UChar or ClangTypeKind.CharS or ClangTypeKind.SChar => "char",
                    ClangTypeKind.Short => "short",
                    ClangTypeKind.Int => "int",
                    ClangTypeKind.Long => "long",
                    ClangTypeKind.LongLong => "long long",
                    ClangTypeKind.UShort => "unsigned short",
                    ClangTypeKind.UInt => "unsigned int",
                    ClangTypeKind.ULong => "unsigned long",
                    ClangTypeKind.ULongLong => "unsigned long long",
                    ClangTypeKind.Double => "double",
                    _ => "float"
                };
                var isUnsigned = kind is ClangTypeKind.CharU or ClangTypeKind.UChar
                    or ClangTypeKind.UShort or ClangTypeKind.UInt
                    or ClangTypeKind.ULong or ClangTypeKind.ULongLong;
                return new CBindingType(
                    CBindingTypeKind.Primitive,
                    name,
                    spelling,
                    IsUnsigned: isUnsigned,
                    IsConst: isConst,
                    Size: ComputeSize(normalized));
            }

            case ClangTypeKind.Pointer:
            {
                var pointee = _api.GetPointeeType(normalized);
                var pointeeKind = (ClangTypeKind)pointee.Kind;

                // int (*fn)(int)：参数类型是 Pointer，其 pointee 是函数类型 → 归一为 FunctionPointer。
                if (pointeeKind is ClangTypeKind.FunctionProto or ClangTypeKind.FunctionNoProto)
                    return ConvertFunctionPointerType(session, pointee, spelling);

                var pointerDepth = spelling.Count(static ch => ch == '*');
                var baseName = spelling.Replace("*", "", StringComparison.Ordinal).Trim();
                var pointeeConst = _api.IsConstQualifiedType(pointee) != 0;
                return new CBindingType(
                    CBindingTypeKind.Pointer,
                    string.IsNullOrEmpty(baseName) ? "void" : baseName,
                    spelling,
                    IsConst: pointeeConst,
                    PointerDepth: pointerDepth);
            }

            case ClangTypeKind.Record:
            {
                var isUnion = spelling.StartsWith("union ", StringComparison.Ordinal);
                var name = spelling.StartsWith("struct ", StringComparison.Ordinal)
                    ? spelling["struct ".Length..]
                    : isUnion ? spelling["union ".Length..] : spelling;
                return new CBindingType(isUnion ? CBindingTypeKind.Union : CBindingTypeKind.Struct, name, spelling, IsConst: isConst);
            }

            case ClangTypeKind.Enum:
                return new CBindingType(
                    CBindingTypeKind.Enum,
                    StripTypePrefix(spelling, "enum "),
                    spelling,
                    IsConst: isConst,
                    Size: ComputeSize(normalized));

            case ClangTypeKind.Typedef:
                return new CBindingType(CBindingTypeKind.Typedef, spelling, spelling, IsConst: isConst);

            case ClangTypeKind.FunctionProto:
            case ClangTypeKind.FunctionNoProto:
                return ConvertFunctionPointerType(session, normalized, spelling);

            case ClangTypeKind.ConstantArray:
            case ClangTypeKind.IncompleteArray:
            {
                var size = kind == ClangTypeKind.ConstantArray ? _api.GetArraySize(normalized) : -1;
                var baseName = StripArraySuffix(spelling);
                return new CBindingType(CBindingTypeKind.Array, baseName, spelling, IsConst: isConst, ArraySize: (int)size);
            }

            default:
                return new CBindingType(CBindingTypeKind.Unknown, spelling, spelling, IsConst: isConst);
        }
    }

    /// <summary>
    /// 函数类型 → FunctionPointer 的 <see cref="CBindingType"/>，携带返回类型与参数类型
    /// （供生成器映射为 Eidos <c>Cfn[...]</c>）。
    /// </summary>
    private CBindingType ConvertFunctionPointerType(ClangSession session, ClangType functionType, string spelling)
    {
        var arity = _api.GetNumArgTypes(functionType);
        var parameterTypes = new List<CBindingType>();
        for (var i = 0; i < arity && i < 16; i++)
            parameterTypes.Add(ConvertType(session, _api.GetArgType(functionType, (uint)i)));

        return new CBindingType(
            CBindingTypeKind.FunctionPointer,
            "function",
            spelling,
            IsConst: _api.IsConstQualifiedType(functionType) != 0,
            FunctionPointerArity: arity < 0 ? 0 : arity,
            FunctionPointerReturnType: ConvertType(session, _api.GetResultType(functionType)),
            FunctionPointerParameterTypes: parameterTypes);
    }

    private int ComputeSize(ClangType type)
    {
        var size = _api.TypeGetSizeOf(type);
        return size < 0 ? 0 : (int)size;
    }

    private ClangType NormalizeType(ClangType type)
    {
        var kind = (ClangTypeKind)type.Kind;
        if (kind is ClangTypeKind.Elaborated or ClangTypeKind.Attributed)
            return _api.GetCanonicalType(type);
        return type;
    }

    private static string StripTypePrefix(string spelling, string prefix) =>
        spelling.StartsWith(prefix, StringComparison.Ordinal) ? spelling[prefix.Length..] : spelling;

    private static string StripArraySuffix(string spelling)
    {
        var bracket = spelling.IndexOf('[');
        return bracket < 0 ? spelling : spelling[..bracket].Trim();
    }

    /// <summary>
    /// 只收集目标头文件自身定义的顶层声明，避免 include 展开带入系统头与内置宏
    /// （内置/预定义宏没有源文件位置，file 为 null，必须跳过）。
    /// 与现有 C extractor（visitor.c is_from_header）的基线名规则一致；
    /// 项目 include 头（如 raylib 多文件）的收编规则在 M5 接线时细化。
    /// </summary>
    private bool IsFromHeader(ClangSession session, ClangCursor cursor, string headerPath)
    {
        _api.GetFileLocation(_api.GetCursorLocation(cursor), out var file, out _, out _, out _);
        if (file == IntPtr.Zero)
            return false;

        var fileName = _api.GetString(_api.GetFileName(file));
        if (string.IsNullOrEmpty(fileName))
            return false;

        var headerBase = Path.GetFileName(headerPath);
        var fileBase = Path.GetFileName(fileName);
        return string.Equals(headerBase, fileBase, StringComparison.OrdinalIgnoreCase);
    }
}
