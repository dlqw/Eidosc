using Eidosc.ProjectSystem;
using System.Security.Cryptography;
using System.Text;

namespace Eidosc.Cli.Lsp;

/// <summary>
/// 管理当前打开的文档状态
/// </summary>
public sealed class LspDocumentManager
{
    private readonly Dictionary<string, LspDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public void OpenDocument(string uri, string text, int version)
    {
        lock (_sync)
        {
            _documents[uri] = CreateDocument(uri, text, version);
        }
    }

    public void UpdateDocument(string uri, string text, int version)
    {
        lock (_sync)
        {
            if (_documents.ContainsKey(uri))
                _documents[uri] = CreateDocument(uri, text, version);
        }
    }

    public void CloseDocument(string uri)
    {
        lock (_sync)
        {
            _documents.Remove(uri);
        }
    }

    public bool TryGetDocument(string uri, out LspDocument? document)
    {
        lock (_sync)
        {
            return _documents.TryGetValue(uri, out document);
        }
    }

    public bool IsOpen(string uri)
    {
        lock (_sync)
        {
            return _documents.ContainsKey(uri);
        }
    }

    public string? GetDocumentText(string uri)
    {
        lock (_sync)
        {
            return _documents.TryGetValue(uri, out var doc) ? doc.Text : null;
        }
    }

    public (string Uri, LspDocument Document)[] GetOpenDocuments()
    {
        lock (_sync)
        {
            return _documents
                .Select(static entry => (entry.Key, CloneDocument(entry.Value)))
                .ToArray();
        }
    }

    private static LspDocument CreateDocument(string uri, string text, int version)
    {
        return new LspDocument
        {
            Uri = uri,
            Text = text,
            Version = version,
            ContentHash = ComputeContentHash(text)
        };
    }

    private static LspDocument CloneDocument(LspDocument document)
    {
        return new LspDocument
        {
            Uri = document.Uri,
            Text = document.Text,
            Version = document.Version,
            ContentHash = document.ContentHash
        };
    }

    private static string ComputeContentHash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

public sealed class LspDocument
{
    public string Uri { get; set; } = "";
    public string Text { get; set; } = "";
    public int Version { get; set; }
    public string ContentHash { get; set; } = "";
}
