using System.Text;

namespace WebDavShare.Core;

/// <summary>一次 HTTP 请求的解析结果。</summary>
public sealed class HttpRequest
{
    public string Method { get; init; } = "";
    public string RawTarget { get; init; } = "";
    public string Path { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = [];
    public bool KeepAlive { get; set; } = true;

    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

    public long ContentLength =>
        long.TryParse(Header("Content-Length"), out var n) ? n : 0;

    public bool IsChunked =>
        string.Equals(Header("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase);
}

public static class HttpParser
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxLineBytes = 8 * 1024;

    /// <summary>从流中读取并解析请求头。返回 null 表示连接正常关闭。</summary>
    public static async Task<HttpRequest?> ReadRequestAsync(
        Stream stream, CancellationToken token)
    {
        var startLine = await ReadLineAsync(stream, token);
        if (startLine is null)
        {
            return null;
        }
        // 容忍 keep-alive 之间的空行
        while (startLine.Length == 0)
        {
            startLine = await ReadLineAsync(stream, token);
            if (startLine is null)
            {
                return null;
            }
        }

        var parts = startLine.Split(' ', 3);
        if (parts.Length < 2)
        {
            throw new InvalidDataException("malformed request line");
        }

        var target = parts[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var consumed = startLine.Length;

        while (true)
        {
            var line = await ReadLineAsync(stream, token);
            if (line is null)
            {
                throw new InvalidDataException("unexpected EOF in headers");
            }
            if (line.Length == 0)
            {
                break;
            }
            consumed += line.Length;
            if (consumed > MaxHeaderBytes)
            {
                throw new InvalidDataException("headers too large");
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            // 重复头按 RFC 合并
            headers[name] = headers.TryGetValue(name, out var prior)
                ? prior + ", " + value
                : value;
        }

        var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";
        var keepAlive = !version.Contains("1.0", StringComparison.Ordinal);
        if (headers.TryGetValue("Connection", out var conn))
        {
            if (conn.Contains("close", StringComparison.OrdinalIgnoreCase))
            {
                keepAlive = false;
            }
            else if (conn.Contains("keep-alive", StringComparison.OrdinalIgnoreCase))
            {
                keepAlive = true;
            }
        }

        return new HttpRequest
        {
            Method = parts[0].ToUpperInvariant(),
            RawTarget = target,
            Path = SplitPath(target),
            Headers = headers,
            KeepAlive = keepAlive,
        };
    }

    private static string SplitPath(string target)
    {
        var cut = target.IndexOfAny(['?', '#']);
        var path = cut >= 0 ? target[..cut] : target;
        // 绝对 URI 形式的请求行（代理风格），取其路径部分
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var slash = path.IndexOf('/', path.IndexOf("//", StringComparison.Ordinal) + 2);
            path = slash >= 0 ? path[slash..] : "/";
        }
        return path.Length == 0 ? "/" : path;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[MaxLineBytes];
        var count = 0;
        var single = new byte[1];

        while (count < MaxLineBytes)
        {
            var read = await stream.ReadAsync(single.AsMemory(0, 1), token);
            if (read == 0)
            {
                return count == 0 ? null : Encoding.UTF8.GetString(buffer, 0, count);
            }
            if (single[0] == (byte)'\n')
            {
                if (count > 0 && buffer[count - 1] == (byte)'\r')
                {
                    count--;
                }
                return Encoding.UTF8.GetString(buffer, 0, count);
            }
            buffer[count++] = single[0];
        }
        throw new InvalidDataException("header line too long");
    }

    /// <summary>读取请求体。必须读干净，否则 keep-alive 的下个请求会错位。</summary>
    public static async Task<byte[]> ReadBodyAsync(
        Stream stream, HttpRequest request, int limit, CancellationToken token)
    {
        if (request.IsChunked)
        {
            using var buffer = new MemoryStream();
            await ReadChunkedAsync(stream, buffer, limit, token);
            return buffer.ToArray();
        }

        var length = request.ContentLength;
        if (length <= 0)
        {
            return [];
        }
        if (length > limit)
        {
            throw new InvalidDataException("body too large");
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return body;
    }

    /// <summary>将请求体流式写入目标（PUT 大文件用，避免整体进内存）。</summary>
    public static async Task<long> CopyBodyAsync(
        Stream source, HttpRequest request, Stream destination, CancellationToken token)
    {
        if (request.IsChunked)
        {
            return await ReadChunkedAsync(source, destination, int.MaxValue, token);
        }

        var remaining = request.ContentLength;
        var total = 0L;
        var buffer = new byte[WebDavServer.CopyBufferSize];
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, want), token);
            if (read == 0)
            {
                throw new IOException("client closed during upload");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            remaining -= read;
            total += read;
        }
        return total;
    }

    private static async Task<long> ReadChunkedAsync(
        Stream source, Stream destination, long limit, CancellationToken token)
    {
        var total = 0L;
        var buffer = new byte[WebDavServer.CopyBufferSize];

        while (true)
        {
            var sizeLine = await ReadLineAsync(source, token);
            if (sizeLine is null)
            {
                break;
            }
            var semi = sizeLine.IndexOf(';');
            var hex = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var size))
            {
                break;
            }
            if (size == 0)
            {
                await ReadLineAsync(source, token);   // 结尾 CRLF
                break;
            }
            total += size;
            if (total > limit)
            {
                throw new InvalidDataException("body too large");
            }

            var remaining = size;
            while (remaining > 0)
            {
                var want = Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, want), token);
                if (read == 0)
                {
                    throw new IOException("client closed during chunked upload");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                remaining -= read;
            }
            await ReadLineAsync(source, token);       // 每块结尾 CRLF
        }
        return total;
    }
}
