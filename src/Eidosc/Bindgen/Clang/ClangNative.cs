using System.Runtime.InteropServices;

namespace Eidosc.Bindgen.Clang;

// =====================================================================
// libclang C API 最小 P/Invoke 封装（in-process 提取层）。
//
// 结构体布局与枚举值以 clang-c/Index.h、clang-c/CXString.h、
// clang-c/CXSourceLocation.h 为权威（本机 clang 22.1.5）。
// 库定位策略与 LlvmCompiler.FindTool 对齐：LLVM_PATH、PATH、标准安装目录。
// =====================================================================

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangString
{
    internal readonly IntPtr Data;
    internal readonly uint PrivateFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangCursor
{
    internal readonly int Kind;
    internal readonly int XData;
    internal readonly IntPtr Data0;
    internal readonly IntPtr Data1;
    internal readonly IntPtr Data2;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangType
{
    internal readonly int Kind;
    internal readonly IntPtr Data0;
    internal readonly IntPtr Data1;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangSourceLocation
{
    internal readonly IntPtr Ptr0;
    internal readonly IntPtr Ptr1;
    internal readonly uint IntData;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangSourceRange
{
    internal readonly IntPtr Ptr0;
    internal readonly IntPtr Ptr1;
    internal readonly uint BeginIntData;
    internal readonly uint EndIntData;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ClangToken
{
    internal readonly uint IntData0;
    internal readonly uint IntData1;
    internal readonly uint IntData2;
    internal readonly uint IntData3;
    internal readonly IntPtr PtrData;
}

internal enum ClangTokenKind : int
{
    Punctuation = 0,
    Keyword = 1,
    Identifier = 2,
    Literal = 3,
    Comment = 4
}

internal enum ClangChildVisitResult : uint
{
    Break = 0,
    Continue = 1,
    Recurse = 2
}

internal enum ClangDiagnosticSeverity : uint
{
    Ignored = 0,
    Note = 1,
    Warning = 2,
    Error = 3,
    Fatal = 4
}

internal enum ClangErrorCode : uint
{
    Success = 0,
    Failure = 1,
    Crashed = 2,
    InvalidArguments = 3,
    AstReadError = 4
}

[Flags]
internal enum ClangTranslationUnitFlags : uint
{
    None = 0,
    DetailedPreprocessingRecord = 0x01,
    SkipFunctionBodies = 0x40
}

internal enum ClangCursorKind : int
{
    UnexposedDecl = 1,
    StructDecl = 2,
    UnionDecl = 3,
    ClassDecl = 4,
    EnumDecl = 5,
    FieldDecl = 6,
    EnumConstantDecl = 7,
    FunctionDecl = 8,
    VarDecl = 9,
    ParmDecl = 10,
    TypedefDecl = 20,
    MacroDefinition = 501
}

internal enum ClangTypeKind : int
{
    Void = 2,
    Bool = 3,
    CharU = 4,
    UChar = 5,
    UShort = 8,
    UInt = 9,
    ULong = 10,
    ULongLong = 11,
    CharS = 13,
    SChar = 14,
    Short = 16,
    Int = 17,
    Long = 18,
    LongLong = 19,
    Float = 21,
    Double = 22,
    Pointer = 101,
    Record = 105,
    Enum = 106,
    Typedef = 107,
    FunctionNoProto = 110,
    FunctionProto = 111,
    ConstantArray = 112,
    IncompleteArray = 114,
    Elaborated = 119,
    Attributed = 163
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr ClangCreateIndexFn(int excludeDeclarationsFromPch, int displayDiagnostics);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ClangDisposeIndexFn(IntPtr index);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangErrorCode ClangParseTranslationUnit2Fn(
    IntPtr index,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceFilename,
    IntPtr commandLineArgs,
    int numCommandLineArgs,
    IntPtr unsavedFiles,
    uint numUnsavedFiles,
    uint options,
    out IntPtr translationUnit);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ClangDisposeTranslationUnitFn(IntPtr translationUnit);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangCursor ClangGetTranslationUnitCursorFn(IntPtr translationUnit);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangChildVisitResult ClangCursorVisitorFn(ClangCursor cursor, ClangCursor parent, IntPtr clientData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint ClangVisitChildrenFn(ClangCursor parent, ClangCursorVisitorFn visitor, IntPtr clientData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangString ClangGetCursorSpellingFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangType ClangGetCursorTypeFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangType ClangGetCursorResultTypeFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangType ClangGetTypedefDeclUnderlyingTypeFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangType ClangGetEnumDeclIntegerTypeFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long ClangGetEnumConstantDeclValueFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangString ClangGetTypeSpellingFn(ClangType type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangType ClangGetCanonicalTypeFn(ClangType type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangType ClangGetPointeeTypeFn(ClangType type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint ClangIsConstQualifiedTypeFn(ClangType type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long ClangGetArraySizeFn(ClangType type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long ClangCursorGetOffsetOfFieldFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long ClangTypeGetSizeOfFn(ClangType type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long ClangTypeGetAlignOfFn(ClangType type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint ClangCursorIsFunctionInlinedFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr ClangGetCStringFn(ClangString str);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ClangDisposeStringFn(ClangString str);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint ClangGetNumDiagnosticsFn(IntPtr translationUnit);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr ClangGetDiagnosticFn(IntPtr translationUnit, uint index);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangDiagnosticSeverity ClangGetDiagnosticSeverityFn(IntPtr diagnostic);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangString ClangGetDiagnosticSpellingFn(IntPtr diagnostic);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ClangDisposeDiagnosticFn(IntPtr diagnostic);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangSourceLocation ClangGetCursorLocationFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ClangGetFileLocationFn(
    ClangSourceLocation location,
    out IntPtr file,
    out uint line,
    out uint column,
    out uint offset);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangString ClangGetFileNameFn(IntPtr file);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangString ClangGetClangVersionFn();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ClangGetNumArgTypesFn(ClangType type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint ClangIsFunctionTypeVariadicFn(ClangType type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint ClangCursorIsVariadicFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr ClangCursorEvaluateFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ClangEvalResultGetKindFn(IntPtr evalResult);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ClangEvalResultGetAsIntFn(IntPtr evalResult);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long ClangEvalResultGetAsLongLongFn(IntPtr evalResult);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr ClangEvalResultGetAsStrFn(IntPtr evalResult);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ClangEvalResultDisposeFn(IntPtr evalResult);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangSourceRange ClangGetCursorExtentFn(ClangCursor cursor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ClangTokenizeFn(
    IntPtr translationUnit,
    ClangSourceRange range,
    out IntPtr tokens,
    out uint numTokens);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ClangGetTokenKindFn(ClangToken token);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate ClangString ClangGetTokenSpellingFn(IntPtr translationUnit, ClangToken token);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ClangDisposeTokensFn(IntPtr translationUnit, IntPtr tokens, uint numTokens);

internal enum ClangEvalResultKind : int
{
    UnExposed = 0,
    Int = 1,
    Float = 2,
    ObjCStrLiteral = 3,
    StrLiteral = 4,
    CFStr = 5,
    Other = 6
}

/// <summary>
/// 一次加载后不可变的 libclang 导出函数集合。全部导出在构造时解析，
/// 任一缺失立即失败（Fail-fast 便于诊断 libclang 版本不匹配）。
/// </summary>
internal sealed class ClangApi
{
    internal ClangApi(string libraryPath, IntPtr library)
    {
        LibraryPath = libraryPath;
        CreateIndex = GetExport<ClangCreateIndexFn>(library, "clang_createIndex");
        DisposeIndex = GetExport<ClangDisposeIndexFn>(library, "clang_disposeIndex");
        ParseTranslationUnit2Raw = GetExport<ClangParseTranslationUnit2Fn>(library, "clang_parseTranslationUnit2");
        DisposeTranslationUnit = GetExport<ClangDisposeTranslationUnitFn>(library, "clang_disposeTranslationUnit");
        GetTranslationUnitCursor = GetExport<ClangGetTranslationUnitCursorFn>(library, "clang_getTranslationUnitCursor");
        VisitChildren = GetExport<ClangVisitChildrenFn>(library, "clang_visitChildren");
        GetCursorSpelling = GetExport<ClangGetCursorSpellingFn>(library, "clang_getCursorSpelling");
        GetCursorType = GetExport<ClangGetCursorTypeFn>(library, "clang_getCursorType");
        GetCursorResultType = GetExport<ClangGetCursorResultTypeFn>(library, "clang_getCursorResultType");
        GetTypedefDeclUnderlyingType = GetExport<ClangGetTypedefDeclUnderlyingTypeFn>(library, "clang_getTypedefDeclUnderlyingType");
        GetEnumDeclIntegerType = GetExport<ClangGetEnumDeclIntegerTypeFn>(library, "clang_getEnumDeclIntegerType");
        GetEnumConstantDeclValue = GetExport<ClangGetEnumConstantDeclValueFn>(library, "clang_getEnumConstantDeclValue");
        GetTypeSpelling = GetExport<ClangGetTypeSpellingFn>(library, "clang_getTypeSpelling");
        GetCanonicalType = GetExport<ClangGetCanonicalTypeFn>(library, "clang_getCanonicalType");
        GetPointeeType = GetExport<ClangGetPointeeTypeFn>(library, "clang_getPointeeType");
        IsConstQualifiedType = GetExport<ClangIsConstQualifiedTypeFn>(library, "clang_isConstQualifiedType");
        GetArraySize = GetExport<ClangGetArraySizeFn>(library, "clang_getArraySize");
        CursorGetOffsetOfField = GetExport<ClangCursorGetOffsetOfFieldFn>(library, "clang_Cursor_getOffsetOfField");
        TypeGetSizeOf = GetExport<ClangTypeGetSizeOfFn>(library, "clang_Type_getSizeOf");
        TypeGetAlignOf = GetExport<ClangTypeGetAlignOfFn>(library, "clang_Type_getAlignOf");
        CursorIsFunctionInlined = GetExport<ClangCursorIsFunctionInlinedFn>(library, "clang_Cursor_isFunctionInlined");
        GetCString = GetExport<ClangGetCStringFn>(library, "clang_getCString");
        DisposeString = GetExport<ClangDisposeStringFn>(library, "clang_disposeString");
        GetNumDiagnostics = GetExport<ClangGetNumDiagnosticsFn>(library, "clang_getNumDiagnostics");
        GetDiagnostic = GetExport<ClangGetDiagnosticFn>(library, "clang_getDiagnostic");
        GetDiagnosticSeverity = GetExport<ClangGetDiagnosticSeverityFn>(library, "clang_getDiagnosticSeverity");
        GetDiagnosticSpelling = GetExport<ClangGetDiagnosticSpellingFn>(library, "clang_getDiagnosticSpelling");
        DisposeDiagnostic = GetExport<ClangDisposeDiagnosticFn>(library, "clang_disposeDiagnostic");
        GetCursorLocation = GetExport<ClangGetCursorLocationFn>(library, "clang_getCursorLocation");
        GetFileLocation = GetExport<ClangGetFileLocationFn>(library, "clang_getFileLocation");
        GetFileName = GetExport<ClangGetFileNameFn>(library, "clang_getFileName");
        GetClangVersion = GetExport<ClangGetClangVersionFn>(library, "clang_getClangVersion");
        GetNumArgTypes = GetExport<ClangGetNumArgTypesFn>(library, "clang_getNumArgTypes");
        IsFunctionTypeVariadic = GetExport<ClangIsFunctionTypeVariadicFn>(library, "clang_isFunctionTypeVariadic");
        CursorIsVariadic = GetExport<ClangCursorIsVariadicFn>(library, "clang_Cursor_isVariadic");
        CursorEvaluate = GetExport<ClangCursorEvaluateFn>(library, "clang_Cursor_Evaluate");
        EvalResultGetKind = GetExport<ClangEvalResultGetKindFn>(library, "clang_EvalResult_getKind");
        EvalResultGetAsInt = GetExport<ClangEvalResultGetAsIntFn>(library, "clang_EvalResult_getAsInt");
        EvalResultGetAsLongLong = GetExport<ClangEvalResultGetAsLongLongFn>(library, "clang_EvalResult_getAsLongLong");
        EvalResultGetAsStr = GetExport<ClangEvalResultGetAsStrFn>(library, "clang_EvalResult_getAsStr");
        EvalResultDispose = GetExport<ClangEvalResultDisposeFn>(library, "clang_EvalResult_dispose");
        GetCursorExtent = GetExport<ClangGetCursorExtentFn>(library, "clang_getCursorExtent");
        Tokenize = GetExport<ClangTokenizeFn>(library, "clang_tokenize");
        GetTokenKind = GetExport<ClangGetTokenKindFn>(library, "clang_getTokenKind");
        GetTokenSpelling = GetExport<ClangGetTokenSpellingFn>(library, "clang_getTokenSpelling");
        DisposeTokens = GetExport<ClangDisposeTokensFn>(library, "clang_disposeTokens");
    }

    internal string LibraryPath { get; }

    internal ClangCreateIndexFn CreateIndex { get; }
    internal ClangDisposeIndexFn DisposeIndex { get; }
    internal ClangParseTranslationUnit2Fn ParseTranslationUnit2Raw { get; }
    internal ClangDisposeTranslationUnitFn DisposeTranslationUnit { get; }
    internal ClangGetTranslationUnitCursorFn GetTranslationUnitCursor { get; }
    internal ClangVisitChildrenFn VisitChildren { get; }
    internal ClangGetCursorSpellingFn GetCursorSpelling { get; }
    internal ClangGetCursorTypeFn GetCursorType { get; }
    internal ClangGetCursorResultTypeFn GetCursorResultType { get; }
    internal ClangGetTypedefDeclUnderlyingTypeFn GetTypedefDeclUnderlyingType { get; }
    internal ClangGetEnumDeclIntegerTypeFn GetEnumDeclIntegerType { get; }
    internal ClangGetEnumConstantDeclValueFn GetEnumConstantDeclValue { get; }
    internal ClangGetTypeSpellingFn GetTypeSpelling { get; }
    internal ClangGetCanonicalTypeFn GetCanonicalType { get; }
    internal ClangGetPointeeTypeFn GetPointeeType { get; }
    internal ClangIsConstQualifiedTypeFn IsConstQualifiedType { get; }
    internal ClangGetArraySizeFn GetArraySize { get; }
    internal ClangCursorGetOffsetOfFieldFn CursorGetOffsetOfField { get; }
    internal ClangTypeGetSizeOfFn TypeGetSizeOf { get; }
    internal ClangTypeGetAlignOfFn TypeGetAlignOf { get; }
    internal ClangCursorIsFunctionInlinedFn CursorIsFunctionInlined { get; }
    internal ClangGetCStringFn GetCString { get; }
    internal ClangDisposeStringFn DisposeString { get; }
    internal ClangGetNumDiagnosticsFn GetNumDiagnostics { get; }
    internal ClangGetDiagnosticFn GetDiagnostic { get; }
    internal ClangGetDiagnosticSeverityFn GetDiagnosticSeverity { get; }
    internal ClangGetDiagnosticSpellingFn GetDiagnosticSpelling { get; }
    internal ClangDisposeDiagnosticFn DisposeDiagnostic { get; }
    internal ClangGetCursorLocationFn GetCursorLocation { get; }
    internal ClangGetFileLocationFn GetFileLocation { get; }
    internal ClangGetFileNameFn GetFileName { get; }
    internal ClangGetClangVersionFn GetClangVersion { get; }
    internal ClangGetNumArgTypesFn GetNumArgTypes { get; }
    internal ClangIsFunctionTypeVariadicFn IsFunctionTypeVariadic { get; }
    internal ClangCursorIsVariadicFn CursorIsVariadic { get; }
    internal ClangCursorEvaluateFn CursorEvaluate { get; }
    internal ClangEvalResultGetKindFn EvalResultGetKind { get; }
    internal ClangEvalResultGetAsIntFn EvalResultGetAsInt { get; }
    internal ClangEvalResultGetAsLongLongFn EvalResultGetAsLongLong { get; }
    internal ClangEvalResultGetAsStrFn EvalResultGetAsStr { get; }
    internal ClangEvalResultDisposeFn EvalResultDispose { get; }
    internal ClangGetCursorExtentFn GetCursorExtent { get; }
    internal ClangTokenizeFn Tokenize { get; }
    internal ClangGetTokenKindFn GetTokenKind { get; }
    internal ClangGetTokenSpellingFn GetTokenSpelling { get; }
    internal ClangDisposeTokensFn DisposeTokens { get; }

    internal string GetString(ClangString str)
    {
        var ptr = GetCString(str);
        var result = ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        DisposeString(str);
        return result;
    }

    /// <summary>
    /// clang_parseTranslationUnit2 的安全封装：参数数组手动按 UTF-8 封送
    /// （委托封送不支持 String[] + LPUTF8Str 组合；LPStr 是 ANSI，会破坏非 ASCII 路径）。
    /// </summary>
    internal ClangErrorCode ParseTranslationUnit2(
        IntPtr index,
        string sourceFilename,
        IReadOnlyList<string>? commandLineArgs,
        uint options,
        out IntPtr translationUnit)
    {
        var count = commandLineArgs?.Count ?? 0;
        var nativeArgs = count == 0 ? [] : new IntPtr[count];
        var allocatedStrings = new List<IntPtr>(count);

        try
        {
            for (var i = 0; i < count; i++)
            {
                var ptr = Marshal.StringToCoTaskMemUTF8(commandLineArgs![i]);
                allocatedStrings.Add(ptr);
                nativeArgs[i] = ptr;
            }

            var handle = GCHandle.Alloc(nativeArgs, GCHandleType.Pinned);
            try
            {
                var argsPointer = count == 0 ? IntPtr.Zero : handle.AddrOfPinnedObject();
                return ParseTranslationUnit2Raw(index, sourceFilename, argsPointer, count, IntPtr.Zero, 0, options, out translationUnit);
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            foreach (var ptr in allocatedStrings)
                Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static T GetExport<T>(IntPtr library, string name) where T : Delegate
    {
        if (NativeLibrary.TryGetExport(library, name, out var address))
            return Marshal.GetDelegateForFunctionPointer<T>(address);

        throw new InvalidOperationException($"libclang export '{name}' not found in loaded library.");
    }
}

/// <summary>
/// libclang 库定位与加载。定位顺序与 LlvmCompiler.FindTool 一致，
/// 并补充标准安装根目录，保证 pkg bind 的 clang 提取与编译/链接使用同一套 LLVM。
/// </summary>
internal static class ClangNative
{
    internal static bool TryLoad(out string? error, out ClangApi? api)
    {
        var path = FindLibrary();
        if (path == null)
        {
            error = "libclang not found. Install LLVM (clang) or set LLVM_PATH; the C→Eidos extractor needs the same libclang as the clang used for compile/link.";
            api = null;
            return false;
        }

        try
        {
            var library = NativeLibrary.Load(path);
            api = new ClangApi(path, library);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            error = $"Failed to load libclang from '{path}': {ex.Message}";
            api = null;
            return false;
        }
    }

    private static string? FindLibrary()
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["libclang.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libclang.dylib"]
                : ["libclang.so"];

        // 1. LLVM_PATH 环境变量（bin 目录）
        var llvmPath = Environment.GetEnvironmentVariable("LLVM_PATH");
        if (!string.IsNullOrWhiteSpace(llvmPath))
        {
            foreach (var name in names)
            {
                var path = Path.Combine(llvmPath, "bin", name);
                if (File.Exists(path))
                    return path;
            }
        }

        // 2. PATH 中的每个目录
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (pathVariable != null)
        {
            foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var name in names)
                {
                    var path = Path.Combine(directory, name);
                    if (File.Exists(path))
                        return path;
                }
            }
        }

        // 3. 标准安装根目录（Windows）与 Unix 库目录
        var roots = OperatingSystem.IsWindows()
            ? new[] { @"C:\Program Files\LLVM", @"D:\Program Files\LLVM" }
            : new[] { "/usr", "/usr/local" };
        foreach (var root in roots)
        {
            var binDirectory = OperatingSystem.IsWindows() ? Path.Combine(root, "bin") : Path.Combine(root, "lib");
            foreach (var name in names)
            {
                var path = Path.Combine(binDirectory, name);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }
}
