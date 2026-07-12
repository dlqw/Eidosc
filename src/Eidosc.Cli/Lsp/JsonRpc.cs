using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Eidosc.Cli.Lsp;

/// <summary>
/// JSON-RPC 2.0 消息读写（基于 Content-Length 头的 stdin/stdout 协议）
/// </summary>
public static class JsonRpc
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<JsonElement?> ReadMessageAsync(Stream input, CancellationToken ct = default)
    {
        // Read Content-Length header
        var contentLength = -1;
        var headerLine = new StringBuilder();

        while (true)
        {
            var b = input.ReadByte();
            if (b < 0) return null; // EOF

            if (b == '\r')
            {
                var next = input.ReadByte();
                if (next == '\n')
                {
                    var line = headerLine.ToString();
                    if (string.IsNullOrEmpty(line))
                    {
                        // Empty line = end of headers
                        break;
                    }

                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                    }

                    headerLine.Clear();
                }
                else
                {
                    headerLine.Append((char)b);
                    if (next >= 0) headerLine.Append((char)next);
                }
            }
            else
            {
                headerLine.Append((char)b);
            }
        }

        if (contentLength <= 0)
            return null;

        var buffer = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            var totalRead = 0;
            while (totalRead < contentLength)
            {
                var read = await input.ReadAsync(buffer, totalRead, contentLength - totalRead, ct);
                if (read <= 0) return null;
                totalRead += read;
            }

            var json = Encoding.UTF8.GetString(buffer, 0, contentLength);
            return JsonSerializer.Deserialize<JsonElement>(json, Options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task WriteMessageAsync(Stream output, object message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message, Options);
        var contentBytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {contentBytes.Length}\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);

        await output.WriteAsync(headerBytes, ct);
        await output.WriteAsync(contentBytes, ct);
        await output.FlushAsync(ct);
    }
}
