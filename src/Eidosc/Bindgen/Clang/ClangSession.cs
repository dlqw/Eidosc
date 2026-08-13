using System.Runtime.InteropServices;

namespace Eidosc.Bindgen.Clang;

/// <summary>
/// 一次头文件解析会话：管理 CXIndex 与 CXTranslationUnit 生命周期，
/// 提供诊断、根游标遍历与 CXString 辅助。M1 只负责会话与遍历骨架，
/// 语义提取（CHeaderIr）由后续里程碑在此之上实现。
/// </summary>
internal sealed class ClangSession : IDisposable
{
    private readonly ClangApi _api;
    private IntPtr _index;
    private IntPtr _translationUnit;

    internal ClangSession(ClangApi api)
    {
        _api = api;
        _index = api.CreateIndex(0, 0);
        if (_index == IntPtr.Zero)
            throw new InvalidOperationException("clang_createIndex failed.");
    }

    internal string LibraryPath => _api.LibraryPath;

    internal string ClangVersion => _api.GetString(_api.GetClangVersion());

    internal ClangApi Api => _api;

    internal void Parse(
        string headerPath,
        IReadOnlyList<string>? includePaths = null,
        IReadOnlyList<string>? defines = null,
        bool skipFunctionBodies = false)
    {
        if (_translationUnit != IntPtr.Zero)
        {
            _api.DisposeTranslationUnit(_translationUnit);
            _translationUnit = IntPtr.Zero;
        }

        var arguments = new List<string> { "-x", "c" };
        if (defines != null)
        {
            foreach (var define in defines)
                arguments.Add($"-D{define}");
        }

        if (includePaths != null)
        {
            foreach (var includePath in includePaths)
            {
                arguments.Add("-I");
                arguments.Add(includePath);
            }
        }

        var options = ClangTranslationUnitFlags.DetailedPreprocessingRecord;
        if (skipFunctionBodies)
            options |= ClangTranslationUnitFlags.SkipFunctionBodies;

        var error = _api.ParseTranslationUnit2(
            _index,
            headerPath,
            arguments,
            (uint)options,
            out _translationUnit);
        if (error != ClangErrorCode.Success)
            throw new InvalidOperationException($"clang_parseTranslationUnit2 failed with {error}.");

        if (_translationUnit == IntPtr.Zero)
            throw new InvalidOperationException("clang_parseTranslationUnit2 returned a null translation unit.");
    }

    /// <summary>
    /// 按严重级别收集诊断文本（"severity: message"）。错误/致命诊断通过
    /// <see cref="HasErrors"/> 单独暴露，供调用方决定是否继续提取。
    /// </summary>
    internal IReadOnlyList<string> Diagnostics
    {
        get
        {
            var result = new List<string>();
            if (_translationUnit == IntPtr.Zero)
                return result;

            var count = _api.GetNumDiagnostics(_translationUnit);
            for (uint i = 0; i < count; i++)
            {
                var diagnostic = _api.GetDiagnostic(_translationUnit, i);
                try
                {
                    var severity = _api.GetDiagnosticSeverity(diagnostic);
                    var message = _api.GetString(_api.GetDiagnosticSpelling(diagnostic));
                    result.Add($"{severity}: {message}");
                }
                finally
                {
                    _api.DisposeDiagnostic(diagnostic);
                }
            }

            return result;
        }
    }

    internal bool HasErrors
    {
        get
        {
            if (_translationUnit == IntPtr.Zero)
                return false;

            var count = _api.GetNumDiagnostics(_translationUnit);
            for (uint i = 0; i < count; i++)
            {
                var diagnostic = _api.GetDiagnostic(_translationUnit, i);
                try
                {
                    if (_api.GetDiagnosticSeverity(diagnostic) >= ClangDiagnosticSeverity.Error)
                        return true;
                }
                finally
                {
                    _api.DisposeDiagnostic(diagnostic);
                }
            }

            return false;
        }
    }

    internal ClangCursor RootCursor => _api.GetTranslationUnitCursor(_translationUnit);

    internal uint VisitChildren(ClangCursor parent, ClangCursorVisitorFn visitor, IntPtr clientData) =>
        _api.VisitChildren(parent, visitor, clientData);

    internal string GetCursorSpelling(ClangCursor cursor) => _api.GetString(_api.GetCursorSpelling(cursor));

    internal string GetTypeSpelling(ClangType type) => _api.GetString(_api.GetTypeSpelling(type));

    /// <summary>
    /// 返回游标 extent 的词法 token（kind + spelling）。用于宏常量求值回退路径。
    /// </summary>
    internal IReadOnlyList<(ClangTokenKind Kind, string Spelling)> TokenizeCursor(ClangCursor cursor)
    {
        var range = _api.GetCursorExtent(cursor);
        _api.Tokenize(_translationUnit, range, out var tokens, out var count);
        try
        {
            var result = new List<(ClangTokenKind, string)>(checked((int)count));
            for (var i = 0; i < count; i++)
            {
                var token = Marshal.PtrToStructure<ClangToken>(IntPtr.Add(tokens, checked(i * Marshal.SizeOf<ClangToken>())));
                result.Add(((ClangTokenKind)_api.GetTokenKind(token), _api.GetString(_api.GetTokenSpelling(_translationUnit, token))));
            }

            return result;
        }
        finally
        {
            _api.DisposeTokens(_translationUnit, tokens, count);
        }
    }

    public void Dispose()
    {
        if (_translationUnit != IntPtr.Zero)
        {
            _api.DisposeTranslationUnit(_translationUnit);
            _translationUnit = IntPtr.Zero;
        }

        if (_index != IntPtr.Zero)
        {
            _api.DisposeIndex(_index);
            _index = IntPtr.Zero;
        }
    }
}
