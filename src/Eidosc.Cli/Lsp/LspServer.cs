using Eidosc.ProjectSystem;
using System.Text.Json;
using Eidosc.Cli.Commands;
using Eidosc.Cli.Resources;
using Eidosc.CodeFormatting;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.Query;
using Eidosc.BuildSystem;

namespace Eidosc.Cli.Lsp;

/// <summary>
/// LSP 语言服务器主循环
/// </summary>
public sealed class LspServer : IDisposable
{
    private sealed record SnapshotEntry(
        IdeSemanticSnapshot Snapshot,
        LspSemanticMapper.SnapshotIndex Index,
        int Version,
        string ContentHash,
        DependencyState DependencyState,
        SnapshotDerivedCache DerivedCache);

    private sealed class SnapshotDerivedCache
    {
        private readonly object _lock = new();
        private List<LspDiagnostic>? _diagnostics;
        private readonly Dictionary<string, LspSemanticTokens> _semanticTokens = new(StringComparer.Ordinal);

        public List<LspDiagnostic> GetDiagnostics(IdeSemanticSnapshot snapshot)
        {
            lock (_lock)
            {
                return _diagnostics ??= LspSemanticMapper.MapDiagnostics(snapshot);
            }
        }

        public LspSemanticTokens GetSemanticTokens(
            IdeSemanticSnapshot snapshot,
            LspSemanticMapper.SnapshotIndex index,
            string? documentFilePath,
            string? sourceText)
        {
            var key = string.Join('\0', documentFilePath ?? "", ContentHash.ComputeHash(sourceText ?? ""));
            lock (_lock)
            {
                if (_semanticTokens.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var mapped = LspSemanticMapper.MapSemanticTokens(snapshot, index, documentFilePath, sourceText);
                _semanticTokens[key] = mapped;
                return mapped;
            }
        }
    }

    private readonly record struct DependencyStamp(string RelevantInputFingerprint);

    private sealed record DependencyState(
        DependencyStamp Stamp,
        IReadOnlyDictionary<string, string> ImportedSourceFingerprints);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly LspDocumentManager _documents = new();
    private readonly string[] _importRoots;
    private readonly Func<string, string, IdeSemanticSnapshot>? _compileDocumentOverride;
    private readonly CancellationTokenSource _cts = new();
    private readonly LspDependencyFingerprintCache _dependencyFingerprintCache = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _snapshotLock = new();
    private readonly object _diagnosticLock = new();
    private readonly PipelineQuerySession _querySession = new();
    private readonly Dictionary<string, SnapshotEntry> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _snapshotBuildLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _diagnosticCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _diagnosticTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _diagnosticDebounce;
    private readonly LspPerformanceMetrics _performanceMetrics = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public LspServer(
        Stream input,
        Stream output,
        string[] importRoots,
        Func<string, string, IdeSemanticSnapshot>? compileDocumentOverride = null,
        TimeSpan? diagnosticDebounce = null)
    {
        _input = input;
        _output = output;
        _importRoots = importRoots;
        _compileDocumentOverride = compileDocumentOverride;
        _diagnosticDebounce = diagnosticDebounce ?? TimeSpan.FromMilliseconds(350);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                var message = await JsonRpc.ReadMessageAsync(_input, linkedCts.Token);
                if (message == null)
                    break;

                await HandleMessageAsync(message.Value, linkedCts.Token);
            }
        }
        finally
        {
            _cts.Cancel();
            try
            {
                await FlushPendingDiagnosticsAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    public void Dispose()
    {
        _dependencyFingerprintCache.Dispose();
        _cts.Dispose();
    }

    private async Task HandleMessageAsync(JsonElement message, CancellationToken ct)
    {
        if (!message.TryGetProperty("method", out var methodProp))
            return;

        var method = methodProp.GetString();
        var id = message.TryGetProperty("id", out var idProp) ? idProp.ValueKind != JsonValueKind.Null ? idProp : (JsonElement?)null : null;
        var isRequest = id.HasValue;

        switch (method)
        {
            case "initialize":
                await SendResponseAsync(id!.Value, new
                {
                    capabilities = new LspServerCapabilities()
                }, ct);
                break;

            case "initialized":
                // Notification, no response needed
                break;

            case "shutdown":
                await SendResponseAsync(id!.Value, true, ct);
                break;

            case "exit":
                _cts.Cancel();
                break;

            case "textDocument/didOpen":
                await HandleDidOpenAsync(message, ct);
                break;

            case "textDocument/didChange":
                await HandleDidChangeAsync(message, ct);
                break;

            case "textDocument/didClose":
                await HandleDidCloseAsync(message, ct);
                break;

            case "textDocument/completion":
                if (isRequest)
                    await HandleCompletionAsync(id!.Value, message, ct);
                break;

            case "textDocument/hover":
                if (isRequest)
                    await HandleHoverAsync(id!.Value, message, ct);
                break;

            case "textDocument/definition":
                if (isRequest)
                    await HandleDefinitionAsync(id!.Value, message, ct);
                break;

            case "textDocument/declaration":
            case "textDocument/typeDefinition":
            case "textDocument/implementation":
                if (isRequest)
                    await HandleDefinitionAsync(id!.Value, message, ct);
                break;

            case "textDocument/references":
                if (isRequest)
                    await HandleReferencesAsync(id!.Value, message, ct);
                break;

            case "textDocument/documentSymbol":
                if (isRequest)
                    await HandleDocumentSymbolAsync(id!.Value, message, ct);
                break;

            case "textDocument/codeAction":
                if (isRequest)
                    await HandleCodeActionAsync(id!.Value, message, ct);
                break;

            case "textDocument/formatting":
                if (isRequest)
                    await HandleFormattingAsync(id!.Value, message, ct);
                break;

            case "textDocument/inlayHint":
                if (isRequest)
                    await HandleInlayHintAsync(id!.Value, message, ct);
                break;

            case "textDocument/semanticTokens/full":
                if (isRequest)
                    await HandleSemanticTokensAsync(id!.Value, message, ct);
                break;

            case "eidos/proofStates":
                if (isRequest)
                    await HandleProofStatesAsync(id!.Value, message, ct);
                break;

            case "eidos/proofSearch":
                if (isRequest)
                    await HandleProofSearchAsync(id!.Value, message, ct);
                break;

            case "eidos/patternCoverageExplain":
                if (isRequest)
                    await HandlePatternCoverageExplainAsync(id!.Value, message, ct);
                break;

            case "eidos/generatedDocument":
                if (isRequest)
                    await HandleGeneratedDocumentAsync(id!.Value, message, ct);
                break;

            default:
                if (isRequest)
                    await SendResponseAsync(id!.Value, null, ct);
                break;
        }
    }

    private async Task HandleDidOpenAsync(JsonElement message, CancellationToken ct)
    {
        var textDoc = message.GetProperty("params").GetProperty("textDocument");
        var uri = textDoc.GetProperty("uri").GetString() ?? "";
        var text = textDoc.GetProperty("text").GetString() ?? "";
        var version = textDoc.GetProperty("version").GetInt32();

        _documents.OpenDocument(uri, text, version);
        UpdateSourceOverlayAndInvalidate(uri, text, version);
        ScheduleDiagnostics(uri, text, version, ct);
    }

    private async Task HandleDidChangeAsync(JsonElement message, CancellationToken ct)
    {
        var params_ = message.GetProperty("params");
        var textDoc = params_.GetProperty("textDocument");
        var uri = textDoc.GetProperty("uri").GetString() ?? "";
        var version = textDoc.GetProperty("version").GetInt32();

        if (!params_.TryGetProperty("contentChanges", out var changes) ||
            !TryGetFullDocumentText(changes, out var text))
        {
            CancelPendingDiagnostics(uri);
            lock (_snapshotLock)
            {
                _snapshots.Remove(uri);
            }
            await PublishDiagnosticsAsync(
                uri,
                [CreateServerDiagnostic("E-LSP0002", CliMessages.LspFullDocumentSyncExpected)],
                version,
                ct);
            return;
        }

        _documents.UpdateDocument(uri, text, version);
        UpdateSourceOverlayAndInvalidate(uri, text, version);
        ScheduleDiagnostics(uri, text, version, ct);
    }

    private async Task HandleDidCloseAsync(JsonElement message, CancellationToken ct)
    {
        var textDoc = message.GetProperty("params").GetProperty("textDocument");
        var uri = textDoc.GetProperty("uri").GetString() ?? "";
        _documents.CloseDocument(uri);
        RemoveSourceOverlayAndInvalidate(uri);
        CancelPendingDiagnostics(uri);
        lock (_snapshotLock)
        {
            _snapshots.Remove(uri);
            _snapshotBuildLocks.Remove(uri);
        }
        await PublishDiagnosticsAsync(uri, [], version: null, ct);
    }

    private async Task HandleCompletionAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var (uri, line, character) = GetTextDocumentPosition(message);
        var snapshot = GetOrCompileSnapshot(uri);
        var text = _documents.GetDocumentText(uri);
        var items = snapshot != null
            ? LspSemanticMapper.MapCompletions(snapshot, line, character, text)
            : [];

        await SendResponseAsync(id, new { isIncomplete = false, items }, ct);
    }

    private async Task HandleHoverAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var (uri, line, character) = GetTextDocumentPosition(message);
        var snapshot = GetOrCompileSnapshotEntry(uri);
        var text = _documents.GetDocumentText(uri);
        var hover = snapshot != null
            ? LspSemanticMapper.MapHover(snapshot.Snapshot, snapshot.Index, line, character, text)
            : null;
        await SendResponseAsync(id, hover, ct);
    }

    private async Task HandleDefinitionAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var (uri, line, character) = GetTextDocumentPosition(message);
        var snapshot = GetOrCompileSnapshotEntry(uri);
        var location = snapshot != null ? LspSemanticMapper.MapDefinition(snapshot.Snapshot, snapshot.Index, line, character) : null;
        await SendResponseAsync(id, location, ct);
    }

    private async Task HandleReferencesAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var (uri, line, character) = GetTextDocumentPosition(message);
        var snapshot = GetOrCompileSnapshotEntry(uri);
        var locations = snapshot != null ? LspSemanticMapper.MapReferences(snapshot.Snapshot, snapshot.Index, line, character) : [];
        await SendResponseAsync(id, locations, ct);
    }

    private async Task HandleDocumentSymbolAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var uri = message.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
        var snapshot = GetOrCompileSnapshot(uri);
        var symbols = snapshot != null
            ? LspSemanticMapper.MapDocumentSymbols(snapshot, UriToFilePath(uri))
            : [];

        await SendResponseAsync(id, symbols, ct);
    }

    private async Task HandleCodeActionAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var params_ = message.GetProperty("params");
        var uri = params_.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
        if (string.Equals(Path.GetFileName(UriToFilePath(uri)), EidosProjectConfigurationLoader.DefaultFileName, StringComparison.OrdinalIgnoreCase) &&
            _documents.GetDocumentText(uri) is { } manifestText &&
            TryGetRange(params_, out var manifestRange))
        {
            var manifestActions = LspManifestNamingMapper.MapCodeActions(
                manifestText,
                UriToFilePath(uri),
                uri,
                manifestRange,
                _documents.GetOpenDocuments().ToDictionary(
                    static document => Path.GetFullPath(UriToFilePath(document.Uri)),
                    static document => document.Document.Text,
                    OperatingSystem.IsWindows()
                        ? StringComparer.OrdinalIgnoreCase
                        : StringComparer.Ordinal));
            await SendResponseAsync(id, manifestActions, ct);
            return;
        }

        var snapshot = GetOrCompileSnapshot(uri);
        var actions = snapshot != null && TryGetRange(params_, out var range)
            ? LspSemanticMapper.MapCodeActions(snapshot, uri, UriToFilePath(uri), range)
            : [];

        await SendResponseAsync(id, actions, ct);
    }

    private async Task HandleFormattingAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var params_ = message.GetProperty("params");
        var textDoc = params_.GetProperty("textDocument");
        var uri = textDoc.GetProperty("uri").GetString() ?? "";
        var text = _documents.GetDocumentText(uri);
        if (text == null)
        {
            await SendResponseAsync(id, Array.Empty<LspTextEdit>(), ct);
            return;
        }

        var tabSize = 4;
        if (params_.TryGetProperty("options", out var options) &&
            options.TryGetProperty("tabSize", out var tabSizeElement) &&
            tabSizeElement.ValueKind == JsonValueKind.Number)
        {
            tabSize = Math.Max(1, tabSizeElement.GetInt32());
        }

        var result = EidosFormatter.Format(text, UriToFilePath(uri), new EidosFormatterOptions
        {
            IndentSize = tabSize,
            LanguageVersion = EidosProjectConfigurationLoader.TryLoadNearest(UriToFilePath(uri))?
                .Configuration.LanguageVersion ?? EidosLanguageVersions.DefaultForExistingProjects
        });
        if (!result.Success || string.Equals(text, result.FormattedText, StringComparison.Ordinal))
        {
            await SendResponseAsync(id, Array.Empty<LspTextEdit>(), ct);
            return;
        }

        await SendResponseAsync(id, new[]
        {
            new LspTextEdit
            {
                Range = GetFullDocumentRange(text),
                NewText = result.FormattedText
            }
        }, ct);
    }

    private async Task HandleInlayHintAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var params_ = message.GetProperty("params");
        var uri = params_.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
        var snapshot = GetOrCompileSnapshotEntry(uri);
        var text = _documents.GetDocumentText(uri);
        LspRange? range = TryGetRange(params_, out var requestedRange)
            ? requestedRange
            : null;
        var hints = snapshot != null
            ? LspSemanticMapper.MapInlayHints(snapshot.Snapshot, snapshot.Index, UriToFilePath(uri), text, range)
            : [];

        await SendResponseAsync(id, hints, ct);
    }

    private async Task HandleSemanticTokensAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var uri = message.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
        var snapshot = GetOrCompileSnapshotEntry(uri);
        var text = _documents.GetDocumentText(uri);
        var tokens = snapshot != null
            ? snapshot.DerivedCache.GetSemanticTokens(snapshot.Snapshot, snapshot.Index, UriToFilePath(uri), text)
            : new LspSemanticTokens();

        await SendResponseAsync(id, tokens, ct);
    }

    private async Task HandleProofStatesAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var uri = message.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
        var snapshot = GetOrCompileSnapshot(uri);
        // Proof states removed during proof migration
        await SendResponseAsync(id, Array.Empty<object>(), ct);
    }

    private async Task HandleProofSearchAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        // Proof search removed during proof migration
        await SendResponseAsync(id, Array.Empty<object>(), ct);
    }

    private async Task HandlePatternCoverageExplainAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var params_ = message.GetProperty("params");
        var uri = params_.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
        LspRange? requestedRange = TryGetRange(params_, out var range)
            ? range
            : null;
        var snapshot = GetOrCompileSnapshot(uri);
        var report = snapshot == null
            ? new LspPatternCoverageExplainReport { InputFile = UriToFilePath(uri) }
            : LspSemanticMapper.MapPatternCoverageExplain(snapshot, UriToFilePath(uri), requestedRange);
        await SendResponseAsync(id, report, ct);
    }

    private async Task HandleGeneratedDocumentAsync(JsonElement id, JsonElement message, CancellationToken ct)
    {
        var params_ = message.GetProperty("params");
        var uri = params_.TryGetProperty("uri", out var uriElement)
            ? uriElement.GetString() ?? string.Empty
            : string.Empty;
        IdeGeneratedDocumentEntry? document;
        lock (_snapshotLock)
        {
            document = _snapshots.Values
                .OrderBy(static entry => entry.Snapshot.InputFile, StringComparer.Ordinal)
                .SelectMany(static entry => entry.Snapshot.GeneratedDocuments)
                .FirstOrDefault(candidate => string.Equals(candidate.Uri, uri, StringComparison.Ordinal));
        }

        await SendResponseAsync(id, document, ct);
    }


    private IdeSemanticSnapshot? GetOrCompileSnapshot(string uri)
    {
        return GetOrCompileSnapshotEntry(uri)?.Snapshot;
    }

    private SnapshotEntry? GetOrCompileSnapshotEntry(string uri)
    {
        var accessStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!_documents.TryGetDocument(uri, out var document) || document == null)
            {
                return null;
            }

            var buildLock = GetSnapshotBuildLock(uri);
            lock (buildLock)
            {
                var dependencyState = CreateDependencyState(uri);
                if (TryGetCurrentSnapshotEntry(uri, document.Version, document.ContentHash, dependencyState.Stamp, out var cached))
                {
                    _performanceMetrics.RecordCacheHit();
                    return cached;
                }

                InvalidateChangedImportedSources(uri, dependencyState);
                var compiled = CompileDocument(uri, document.Text, document.Version);
                _performanceMetrics.RecordCompile();
                dependencyState = CreateDependencyState(uri);
                var compiledEntry = new SnapshotEntry(
                    compiled,
                    new LspSemanticMapper.SnapshotIndex(compiled),
                    document.Version,
                    document.ContentHash,
                    dependencyState,
                    new SnapshotDerivedCache());
                if (_documents.TryGetDocument(uri, out var currentDocument) &&
                    currentDocument != null &&
                    currentDocument.Version == document.Version &&
                    string.Equals(currentDocument.ContentHash, document.ContentHash, StringComparison.Ordinal))
                {
                    lock (_snapshotLock)
                    {
                        _snapshots[uri] = compiledEntry;
                    }
                }

                return compiledEntry;
            }
        }
        finally
        {
            _performanceMetrics.RecordSnapshotAccess(
                System.Diagnostics.Stopwatch.GetElapsedTime(accessStarted));
        }
    }

    private object GetSnapshotBuildLock(string uri)
    {
        lock (_snapshotLock)
        {
            if (_snapshotBuildLocks.TryGetValue(uri, out var buildLock))
            {
                return buildLock;
            }

            buildLock = new object();
            _snapshotBuildLocks[uri] = buildLock;
            return buildLock;
        }
    }

    private bool TryGetCurrentSnapshotEntry(
        string uri,
        int version,
        string contentHash,
        DependencyStamp dependencyStamp,
        out SnapshotEntry? entry)
    {
        lock (_snapshotLock)
        {
            if (_snapshots.TryGetValue(uri, out var current) &&
                current.Version == version &&
                string.Equals(current.ContentHash, contentHash, StringComparison.Ordinal) &&
                current.DependencyState.Stamp == dependencyStamp)
            {
                entry = current;
                return true;
            }
        }

        entry = null;
        return false;
    }

    private void InvalidateChangedImportedSources(string uri, DependencyState currentState)
    {
        SnapshotEntry? previous;
        lock (_snapshotLock)
        {
            _snapshots.TryGetValue(uri, out previous);
        }

        if (previous == null)
        {
            return;
        }

        var previousFingerprints = previous.DependencyState.ImportedSourceFingerprints;
        foreach (var sourcePath in previousFingerprints.Keys
                     .Concat(currentState.ImportedSourceFingerprints.Keys)
                     .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            previousFingerprints.TryGetValue(sourcePath, out var previousFingerprint);
            currentState.ImportedSourceFingerprints.TryGetValue(sourcePath, out var currentFingerprint);
            if (!string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                _querySession.InvalidateSource(sourcePath);
            }
        }
    }

    private void UpdateSourceOverlayAndInvalidate(string uri, string text, int version)
    {
        var filePath = UriToFilePath(uri);
        var affected = _querySession.GetAffectedModules(filePath);
        _querySession.SetSourceOverlay(filePath, text, version);
        _querySession.InvalidateSource(filePath);
        InvalidateSnapshots(affected);
    }

    internal LspPerformanceSnapshot GetPerformanceSnapshot() =>
        _performanceMetrics.CreateSnapshot(_dependencyFingerprintCache.DirectoryScanCount);

    private void RemoveSourceOverlayAndInvalidate(string uri)
    {
        var filePath = UriToFilePath(uri);
        var affected = _querySession.GetAffectedModules(filePath);
        _querySession.RemoveSourceOverlay(filePath);
        _querySession.InvalidateSource(filePath);
        InvalidateSnapshots(affected);
    }

    private void InvalidateSnapshots(IReadOnlySet<string> affectedSourcePaths)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var affected = new HashSet<string>(affectedSourcePaths, comparer);
        lock (_snapshotLock)
        {
            foreach (var uri in _snapshots.Keys
                         .Where(uri => affected.Contains(SourcePathNormalizer.Normalize(UriToFilePath(uri))))
                         .ToArray())
            {
                _snapshots.Remove(uri);
            }
        }
    }

    private void ScheduleDiagnostics(string uri, string text, int version, CancellationToken ct)
    {
        CancelPendingDiagnostics(uri);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var task = Task.Run(() => RunDiagnosticsAfterDelayAsync(uri, text, version, cts));
        lock (_diagnosticLock)
        {
            _diagnosticCancellations[uri] = cts;
            _diagnosticTasks[uri] = task;
        }
    }

    private async Task RunDiagnosticsAfterDelayAsync(
        string uri,
        string text,
        int version,
        CancellationTokenSource diagnosticCts)
    {
        try
        {
            var token = diagnosticCts.Token;
            if (_diagnosticDebounce > TimeSpan.Zero)
            {
                await Task.Delay(_diagnosticDebounce, token);
            }

            await CompileAndPublishDiagnosticsAsync(uri, text, version, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_diagnosticLock)
            {
                if (_diagnosticCancellations.TryGetValue(uri, out var current) &&
                    ReferenceEquals(current, diagnosticCts))
                {
                    _diagnosticCancellations.Remove(uri);
                    _diagnosticTasks.Remove(uri);
                }
            }

            diagnosticCts.Dispose();
        }
    }

    private void CancelPendingDiagnostics(string uri)
    {
        CancellationTokenSource? cts = null;
        lock (_diagnosticLock)
        {
            if (_diagnosticCancellations.Remove(uri, out var current))
            {
                cts = current;
            }
            _diagnosticTasks.Remove(uri);
        }

        cts?.Cancel();
    }

    private async Task FlushPendingDiagnosticsAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task[] tasks;
            lock (_diagnosticLock)
            {
                tasks = [.. _diagnosticTasks.Values];
            }

            if (tasks.Length == 0)
            {
                return;
            }

            await Task.WhenAll(tasks).WaitAsync(ct);
        }
    }

    private async Task CompileAndPublishDiagnosticsAsync(string uri, string text, int version, CancellationToken ct)
    {
        if (!IsCurrentDocumentVersion(uri, version, text))
        {
            return;
        }

        var filePath = UriToFilePath(uri);
        if (string.Equals(Path.GetFileName(filePath), EidosProjectConfigurationLoader.DefaultFileName, StringComparison.OrdinalIgnoreCase))
        {
            await PublishDiagnosticsAsync(uri, LspManifestNamingMapper.Map(text, filePath), version, ct);
            return;
        }

        var snapshotEntry = GetOrCompileSnapshotEntry(uri);
        if (snapshotEntry == null)
        {
            return;
        }

        if (!IsCurrentDocumentVersion(uri, version, text))
        {
            return;
        }

        await PublishDiagnosticsAsync(uri, snapshotEntry.DerivedCache.GetDiagnostics(snapshotEntry.Snapshot), version, ct);
    }

    private DependencyState CreateDependencyState(string uri)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var builder = new System.Text.StringBuilder();
        var importedSourceFingerprints = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var filePath = UriToFilePath(uri);

        var project = EidosProjectConfigurationLoader.TryLoadNearest(filePath);
        if (project != null)
        {
            AppendFileFingerprint(builder, project.FilePath);
            AppendFileFingerprint(builder, Path.Combine(project.ProjectDirectory, "eidos.lock.json"));
            AppendBuildDependencyFingerprints(builder, project);
        }

        foreach (var importedSourcePath in _querySession.GetImportedSourcePaths(filePath)
                     .Order(StringComparer.Ordinal))
        {
            builder.Append("import:");
            builder.AppendLine(importedSourcePath);
            string fingerprint;
            if (_querySession.TryGetSourceOverlayStamp(importedSourcePath, out var overlayStamp))
            {
                fingerprint = overlayStamp;
            }
            else
            {
                fingerprint = CreateFileFingerprint(importedSourcePath);
            }

            importedSourceFingerprints[importedSourcePath] = fingerprint;
            builder.AppendLine(fingerprint);
        }

        var stamp = new DependencyStamp(builder.ToString());
        _performanceMetrics.RecordDependencyFingerprint(
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
        return new DependencyState(stamp, importedSourceFingerprints);
    }

    private static string CreateFileFingerprint(string filePath)
    {
        var builder = new System.Text.StringBuilder();
        AppendFileFingerprint(builder, filePath);
        return builder.ToString();
    }

    private static void AppendFileFingerprint(System.Text.StringBuilder builder, string filePath)
    {
        LspDependencyFingerprintCache.AppendFileFingerprint(builder, filePath);
    }

    private void AppendBuildDependencyFingerprints(
        System.Text.StringBuilder builder,
        LoadedEidosProjectConfiguration project)
    {
        var build = project.Configuration.Build;
        if (build == null)
        {
            return;
        }

        builder.AppendLine("build-host-v2");
        AppendFileFingerprint(builder, build.Program);
        foreach (var input in build.FileInputs.Order(StringComparer.Ordinal))
        {
            if (Directory.Exists(input))
            {
                builder.Append(_dependencyFingerprintCache.GetDirectoryFingerprint(input, project.ProjectDirectory));
            }
            else
            {
                AppendFileFingerprint(builder, input);
            }
        }

        foreach (var name in build.Environment.Order(StringComparer.Ordinal))
        {
            builder.Append("build-env:");
            builder.Append(name);
            builder.Append(':');
            builder.AppendLine(Environment.GetEnvironmentVariable(name) ?? "<absent>");
        }

        foreach (var tool in build.Tools.OrderBy(static tool => tool.Name, StringComparer.Ordinal))
        {
            builder.Append("build-tool:");
            builder.Append(tool.Name);
            builder.Append(':');
            builder.AppendLine(tool.Execution);
            AppendFileFingerprint(builder, tool.Path);
        }

        foreach (var url in build.NetworkInputs.Order(StringComparer.Ordinal))
        {
            builder.Append("build-network:");
            builder.AppendLine(url);
        }

        foreach (var capability in build.VolatileCapabilities.Order(StringComparer.Ordinal))
        {
            builder.Append("build-volatile:");
            builder.AppendLine(capability);
        }
    }

    private IdeSemanticSnapshot CompileDocument(string uri, string text, int? version = null)
    {
        var filePath = UriToFilePath(uri);
        try
        {
            if (_compileDocumentOverride != null)
            {
                return _compileDocumentOverride(filePath, text);
            }

            var inputResolution = ResolveLspDocumentInput(filePath);
            var project = EidosProjectConfigurationLoader.TryLoadNearest(filePath);
            var buildHostResult = RunBuildHost(project, inputResolution);
            if (buildHostResult is { Success: false })
            {
                return CreateBuildHostErrorSnapshot(filePath, buildHostResult);
            }

            var baseImportRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                  inputResolution.ImportResolution.EffectiveSearchRoots;

            var options = new CompilationOptions
            {
                InputFile = filePath,
                LanguageVersion = inputResolution.GetLanguageVersion(),
                StopAtPhase = CompilationPhase.Types,
                DebugLevel = Eidosc.Debug.DebugLevel.Minimal,
                UseColors = false,
                ImportSearchRoots = baseImportRoots
                    .Concat(buildHostResult?.GeneratedSourceRoots ?? [])
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .ToArray(),
                PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ??
                                     new Dictionary<string, string[]>(StringComparer.Ordinal),
                NoImplicitPrelude = project?.Configuration.NoImplicitStdlib ?? false,
                BuildHostFingerprint = buildHostResult?.CacheFingerprint,
                BuildGraphFingerprint = buildHostResult?.Graph?.CanonicalHash,
                GeneratedSourceUriMap = buildHostResult?.GeneratedSourceUris ?? new Dictionary<string, string>(),
                MetaConfiguration = project?.Configuration.Meta
            };

            var result = _querySession.Compile(filePath, text, options, version);
            var snapshot = IdeSemanticSnapshotBuilder.Build(result);
            AddBuildGeneratedDocuments(snapshot, buildHostResult);
            return snapshot;
        }
        catch (Exception ex)
        {
            return CreateServerErrorSnapshot(filePath, ex);
        }
    }

    private static EidosBuildHostResult? RunBuildHost(
        LoadedEidosProjectConfiguration? project,
        ProjectCommandInputResolution inputResolution)
    {
        if (project?.Configuration.Build == null)
        {
            return null;
        }

        return EidosBuildHost.RunAsync(new EidosBuildHostOptions
        {
            ProjectDirectory = project.ProjectDirectory,
            Configuration = project.Configuration.Build,
            LanguageVersion = inputResolution.GetLanguageVersion(),
            TargetName = inputResolution.ProjectTarget?.TargetName ?? "main",
            TargetTriple = Eidosc.CodeGen.TargetInfo.Default.Triple,
            ImportSearchRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                inputResolution.ImportResolution.EffectiveSearchRoots,
            PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ??
                                 new Dictionary<string, string[]>(StringComparer.Ordinal),
            NoImplicitPrelude = project.Configuration.NoImplicitStdlib,
            UseCache = true,
            ReleaseProfile = false,
            TraceBuild = false
        }).GetAwaiter().GetResult();
    }

    private static IdeSemanticSnapshot CreateBuildHostErrorSnapshot(
        string filePath,
        EidosBuildHostResult buildHostResult) => new()
        {
            Success = false,
            InputFile = filePath,
            CompletedPhase = "Build",
            SnapshotConfidence = "Fresh",
            Diagnostics = buildHostResult.Diagnostics.Select(static diagnostic => new IdeDiagnosticEntry
            {
                Severity = diagnostic.Level.ToString().ToLowerInvariant(),
                Code = diagnostic.Code,
                Message = $"Build host: {diagnostic.Message}",
                Metadata = new Dictionary<string, string>(diagnostic.Metadata, StringComparer.Ordinal)
                {
                    ["build.host"] = "true"
                },
                Notes = diagnostic.Notes.ToList(),
                Helps = diagnostic.Helps.ToList()
            }).ToList()
        };

    private static void AddBuildGeneratedDocuments(
        IdeSemanticSnapshot snapshot,
        EidosBuildHostResult? buildHostResult)
    {
        if (buildHostResult?.Graph == null)
        {
            return;
        }

        var existingUris = snapshot.GeneratedDocuments
            .Select(static document => document.Uri)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var artifact in buildHostResult.Graph.Artifacts
                     .Where(static artifact =>
                         artifact.Kind == "generated-module" &&
                         !string.IsNullOrWhiteSpace(artifact.SourceUri))
                     .OrderBy(static artifact => artifact.SourceUri, StringComparer.Ordinal))
        {
            if (!existingUris.Add(artifact.SourceUri))
            {
                continue;
            }

            snapshot.GeneratedDocuments.Add(new IdeGeneratedDocumentEntry
            {
                Uri = artifact.SourceUri,
                StableIdentity = $"build:{buildHostResult.Graph.CanonicalHash}:{artifact.ExpectedSha256}",
                GeneratorIdentity = $"build:{buildHostResult.ProgramHash}",
                TargetIdentity = artifact.Target,
                Content = artifact.EmbeddedSource
            });
        }
    }

    private ProjectCommandInputResolution ResolveLspDocumentInput(string filePath)
    {
        try
        {
            var projectPath = EidosProjectConfigurationLoader.TryLoadNearest(filePath)?.FilePath;
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                return ProjectCommandInputResolver.ResolveDocument(
                    filePath,
                    projectPath,
                    targetName: null,
                    _importRoots);
            }
        }
        catch (InvalidOperationException)
        {
        }

        return ProjectCommandInputResolver.Resolve(
            filePath,
            project: null,
            targetName: null,
            _importRoots);
    }

    private bool IsCurrentDocumentVersion(string uri, int version, string text)
    {
        return _documents.TryGetDocument(uri, out var document) &&
               document != null &&
               document.Version == version &&
               string.Equals(document.Text, text, StringComparison.Ordinal);
    }

    private async Task PublishDiagnosticsAsync(
        string uri,
        List<LspDiagnostic> diagnostics,
        int? version,
        CancellationToken ct)
    {
        await SendNotificationAsync("textDocument/publishDiagnostics", new
        {
            uri,
            version,
            diagnostics
        }, ct);
    }

    private static bool TryGetFullDocumentText(JsonElement changes, out string text)
    {
        text = "";
        if (changes.ValueKind != JsonValueKind.Array ||
            changes.GetArrayLength() == 0)
        {
            return false;
        }

        var firstChange = changes[0];
        if (firstChange.TryGetProperty("range", out _) ||
            firstChange.TryGetProperty("rangeLength", out _) ||
            !firstChange.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = textElement.GetString() ?? "";
        return true;
    }

    private static IdeSemanticSnapshot CreateServerErrorSnapshot(string filePath, Exception ex)
    {
        return new IdeSemanticSnapshot
        {
            Success = false,
            InputFile = filePath,
            CompletedPhase = CliMessages.PhaseNone,
            Diagnostics =
            [
                new IdeDiagnosticEntry
                {
                    Severity = "error",
                    Code = "E-LSP0001",
                    Message = CliMessages.LspCompilationFailed(ex.Message),
                    Span = new IdeSpan
                    {
                        StartLine = 0,
                        StartCharacter = 0,
                        EndLine = 0,
                        EndCharacter = 1,
                        FilePath = filePath
                    }
                }
            ]
        };
    }

    private static LspDiagnostic CreateServerDiagnostic(string code, string message)
    {
        return new LspDiagnostic
        {
            Severity = LspDiagnosticSeverity.Error,
            Code = code,
            Source = "eidosc",
            Message = message,
            Range = new LspRange
            {
                Start = new LspPosition(),
                End = new LspPosition { Line = 0, Character = 1 }
            }
        };
    }

    private static (string uri, int line, int character) GetTextDocumentPosition(JsonElement message)
    {
        var params_ = message.GetProperty("params");
        var textDoc = params_.GetProperty("textDocument");
        var uri = textDoc.GetProperty("uri").GetString() ?? "";
        var pos = params_.GetProperty("position");
        var line = pos.GetProperty("line").GetInt32();
        var character = pos.GetProperty("character").GetInt32();
        return (uri, line, character);
    }

    private static bool TryGetRange(JsonElement params_, out LspRange range)
    {
        range = new LspRange();
        if (!params_.TryGetProperty("range", out var rangeElement) ||
            !rangeElement.TryGetProperty("start", out var startElement) ||
            !rangeElement.TryGetProperty("end", out var endElement))
        {
            return false;
        }

        range = new LspRange
        {
            Start = new LspPosition
            {
                Line = startElement.GetProperty("line").GetInt32(),
                Character = startElement.GetProperty("character").GetInt32()
            },
            End = new LspPosition
            {
                Line = endElement.GetProperty("line").GetInt32(),
                Character = endElement.GetProperty("character").GetInt32()
            }
        };
        return true;
    }

    internal static string UriToFilePath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
            string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return parsed.LocalPath;
        }

        return Uri.UnescapeDataString(uri);
    }

    private static LspRange GetFullDocumentRange(string text)
    {
        var line = 0;
        var character = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                line++;
                character = 0;
                continue;
            }

            if (ch == '\n')
            {
                line++;
                character = 0;
                continue;
            }

            character++;
        }

        return new LspRange
        {
            Start = new LspPosition(),
            End = new LspPosition
            {
                Line = line,
                Character = character
            }
        };
    }

    private async Task SendResponseAsync(JsonElement id, object? result, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await JsonRpc.WriteMessageAsync(_output, new
            {
                jsonrpc = "2.0",
                id,
                result
            }, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendNotificationAsync(string method, object @params, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await JsonRpc.WriteMessageAsync(_output, new
            {
                jsonrpc = "2.0",
                method,
                @params
            }, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
