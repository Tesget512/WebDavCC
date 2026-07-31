using System.Globalization;
using System.Net;
using System.Text;

namespace WebDavShare.Core;

public sealed partial class WebDavServer
{
    // ---------------- GET / HEAD ----------------

    private async Task<int> HandleGetAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);

        if (!TryResolve(request.Path, out var local, out var isVirtualRoot))
        {
            return await SendTextAsync(stream, request, 404, "not found", token);
        }
        if (isVirtualRoot)
        {
            return await SendHtmlAsync(stream, request, RenderShareIndex(), token);
        }
        if (Directory.Exists(local))
        {
            return await SendHtmlAsync(stream, request,
                RenderDirectory(request.Path, local!), token);
        }
        if (!File.Exists(local))
        {
            return await SendTextAsync(stream, request, 404, "not found", token);
        }
        return await SendFileAsync(stream, request, local!, token);
    }

    private async Task<int> SendFileAsync(
        Stream stream, HttpRequest request, string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        var size = info.Length;
        var etag = MakeETag(info);
        var lastModified = info.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture);
        var contentType = MimeTypes.ForPath(path);

        if (IsNotModified(request, etag, info.LastWriteTimeUtc))
        {
            return await SendEmptyAsync(stream, request, 304, token,
                $"ETag: {etag}\r\nLast-Modified: {lastModified}\r\n");
        }

        var start = 0L;
        var end = size - 1;
        var partial = false;

        var rangeHeader = request.Header("Range");
        if (rangeHeader is not null)
        {
            var parsed = ParseRange(rangeHeader, size);
            if (parsed is null)
            {
                return await SendEmptyAsync(stream, request, 416, token,
                    $"Content-Range: bytes */{size}\r\n");
            }
            (start, end) = parsed.Value;
            partial = true;
        }

        var length = end - start + 1;
        var headers = new StringBuilder();
        headers.Append($"HTTP/1.1 {(partial ? 206 : 200)} ")
               .Append(ReasonPhrase(partial ? 206 : 200)).Append("\r\n");
        headers.Append($"Content-Type: {contentType}\r\n");
        headers.Append($"Content-Length: {length}\r\n");
        headers.Append("Accept-Ranges: bytes\r\n");
        headers.Append($"ETag: {etag}\r\n");
        headers.Append($"Last-Modified: {lastModified}\r\n");
        if (partial)
        {
            headers.Append($"Content-Range: bytes {start}-{end}/{size}\r\n");
        }
        headers.Append(ConnectionHeader(request));
        headers.Append("\r\n");
        await WriteAsciiAsync(stream, headers.ToString(), token);

        if (request.Method == "HEAD")
        {
            return partial ? 206 : 200;
        }

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, useAsync: true);
        file.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[CopyBufferSize];
        var remaining = length;
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await file.ReadAsync(buffer.AsMemory(0, want), token);
            if (read == 0)
            {
                break;
            }
            await stream.WriteAsync(buffer.AsMemory(0, read), token);
            remaining -= read;
        }
        return partial ? 206 : 200;
    }

    /// <summary>解析 Range 头。仅支持单区间，返回 null 表示不可满足。</summary>
    internal static (long Start, long End)? ParseRange(string header, long size)
    {
        if (size == 0)
        {
            return null;
        }
        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var spec = value[6..].Trim();
        // 多区间请求只取第一段，符合大多数播放器预期
        var comma = spec.IndexOf(',');
        if (comma >= 0)
        {
            spec = spec[..comma].Trim();
        }
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();
        if (startText.Length == 0 && endText.Length == 0)
        {
            return null;
        }

        if (startText.Length == 0)
        {
            // bytes=-N 表示末尾 N 字节
            if (!long.TryParse(endText, out var suffix) || suffix <= 0)
            {
                return null;
            }
            return (Math.Max(0, size - suffix), size - 1);
        }

        if (!long.TryParse(startText, out var start) || start < 0 || start >= size)
        {
            return null;
        }
        var end = size - 1;
        if (endText.Length > 0)
        {
            if (!long.TryParse(endText, out end) || end < start)
            {
                return null;
            }
            end = Math.Min(end, size - 1);
        }
        return (start, end);
    }

    private static bool IsNotModified(HttpRequest request, string etag, DateTime lastModified)
    {
        var ifNoneMatch = request.Header("If-None-Match");
        if (ifNoneMatch is not null)
        {
            if (ifNoneMatch.Trim() == "*")
            {
                return true;
            }
            return ifNoneMatch.Split(',')
                .Any(tag => tag.Trim().TrimStart('W', '/') == etag);
        }

        // Range 请求带 If-Modified-Since 时不能返回 304，否则播放器拿不到数据
        var ifModifiedSince = request.Header("If-Modified-Since");
        if (ifModifiedSince is not null && request.Header("Range") is null &&
            DateTime.TryParse(ifModifiedSince, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var since))
        {
            return TruncateToSeconds(lastModified) <= TruncateToSeconds(since);
        }
        return false;
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    private static string MakeETag(FileInfo info) =>
        $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";

    // ---------------- PUT ----------------

    private async Task<int> HandlePutAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        if (!TryResolve(request.Path, out var local, out var isVirtualRoot) ||
            isVirtualRoot || local is null)
        {
            await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
            return await SendTextAsync(stream, request, 403, "forbidden", token);
        }
        if (Directory.Exists(local))
        {
            await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
            return await SendTextAsync(stream, request, 405, "target is a collection", token);
        }

        var parent = Path.GetDirectoryName(local);
        if (parent is null || !Directory.Exists(parent))
        {
            await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
            return await SendTextAsync(stream, request, 409, "parent collection missing", token);
        }

        var existed = File.Exists(local);
        var temp = local + ".~dav~" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, CopyBufferSize, useAsync: true))
            {
                await HttpParser.CopyBodyAsync(stream, request, output, token);
            }
            File.Move(temp, local, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return await SendEmptyAsync(stream, request, existed ? 204 : 201, token);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主流程
        }
    }

    // ---------------- DELETE / MKCOL ----------------

    private async Task<int> HandleDeleteAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);

        if (!TryResolve(request.Path, out var local, out var isVirtualRoot) ||
            isVirtualRoot || local is null)
        {
            return await SendTextAsync(stream, request, 403, "forbidden", token);
        }
        if (Directory.Exists(local))
        {
            Directory.Delete(local, recursive: true);
        }
        else if (File.Exists(local))
        {
            File.Delete(local);
        }
        else
        {
            return await SendTextAsync(stream, request, 404, "not found", token);
        }
        return await SendEmptyAsync(stream, request, 204, token);
    }

    private async Task<int> HandleMkcolAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        var body = await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
        if (body.Length > 0)
        {
            return await SendTextAsync(stream, request, 415, "body not supported", token);
        }
        if (!TryResolve(request.Path, out var local, out var isVirtualRoot) ||
            isVirtualRoot || local is null)
        {
            return await SendTextAsync(stream, request, 403, "forbidden", token);
        }
        if (Directory.Exists(local) || File.Exists(local))
        {
            return await SendTextAsync(stream, request, 405, "already exists", token);
        }
        var parent = Path.GetDirectoryName(local);
        if (parent is null || !Directory.Exists(parent))
        {
            return await SendTextAsync(stream, request, 409, "parent missing", token);
        }
        Directory.CreateDirectory(local);
        return await SendEmptyAsync(stream, request, 201, token);
    }

    // ---------------- COPY / MOVE ----------------

    private async Task<int> HandleCopyMoveAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
        var isMove = request.Method == "MOVE";

        if (!TryResolve(request.Path, out var source, out var sourceVirtual) ||
            sourceVirtual || source is null ||
            (!File.Exists(source) && !Directory.Exists(source)))
        {
            return await SendTextAsync(stream, request, 404, "not found", token);
        }

        var destinationHeader = request.Header("Destination");
        if (string.IsNullOrEmpty(destinationHeader))
        {
            return await SendTextAsync(stream, request, 400, "missing Destination", token);
        }

        var destinationPath = ExtractPath(destinationHeader);
        if (!TryResolve(destinationPath, out var destination, out var destVirtual) ||
            destVirtual || destination is null)
        {
            return await SendTextAsync(stream, request, 403, "bad destination", token);
        }

        var overwrite = !string.Equals(request.Header("Overwrite"), "F",
            StringComparison.OrdinalIgnoreCase);
        var exists = File.Exists(destination) || Directory.Exists(destination);
        if (exists && !overwrite)
        {
            return await SendTextAsync(stream, request, 412, "destination exists", token);
        }

        var destinationParent = Path.GetDirectoryName(destination);
        if (destinationParent is null || !Directory.Exists(destinationParent))
        {
            return await SendTextAsync(stream, request, 409, "destination parent missing", token);
        }

        // 防止把目录移进自己的子目录
        if (Directory.Exists(source) &&
            destination.StartsWith(source + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return await SendTextAsync(stream, request, 403, "cannot move into itself", token);
        }

        if (exists)
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            else
            {
                File.Delete(destination);
            }
        }

        if (Directory.Exists(source))
        {
            if (isMove)
            {
                Directory.Move(source, destination);
            }
            else
            {
                CopyDirectory(source, destination);
            }
        }
        else if (isMove)
        {
            File.Move(source, destination);
        }
        else
        {
            File.Copy(source, destination);
        }
        return await SendEmptyAsync(stream, request, exists ? 204 : 201, token);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    /// <summary>Destination 头可能是绝对 URI，取其路径部分。</summary>
    private static string ExtractPath(string destination)
    {
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath;
        }
        var cut = destination.IndexOfAny(['?', '#']);
        return cut >= 0 ? destination[..cut] : destination;
    }

    // ---------------- 浏览器目录页 ----------------

    private static async Task<int> SendHtmlAsync(
        Stream stream, HttpRequest request, string html, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(html);
        var headers = "HTTP/1.1 200 OK\r\n"
                      + "Content-Type: text/html; charset=utf-8\r\n"
                      + $"Content-Length: {payload.Length}\r\n"
                      + ConnectionHeader(request)
                      + "\r\n";
        await WriteAsciiAsync(stream, headers, token);
        if (request.Method != "HEAD")
        {
            await stream.WriteAsync(payload, token);
        }
        return 200;
    }

    private string RenderShareIndex()
    {
        var rows = new StringBuilder();
        foreach (var (name, path) in _config.Shares.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            rows.Append($"<li><a href=\"/{Uri.EscapeDataString(name)}/\">&#128193; ")
                .Append(WebUtility.HtmlEncode(name))
                .Append("</a><span class=\"p\">")
                .Append(WebUtility.HtmlEncode(path))
                .Append("</span></li>");
        }
        return Page(Strings.WebSharedFolders, $"<ul>{rows}</ul>");
    }

    private static string RenderDirectory(string urlPath, string localPath)
    {
        var basePath = urlPath.EndsWith('/') ? urlPath : urlPath + "/";
        var rows = new StringBuilder();

        if (basePath != "/")
        {
            var parent = basePath.TrimEnd('/');
            var slash = parent.LastIndexOf('/');
            rows.Append($"<li><a href=\"{(slash > 0 ? parent[..slash] : "/")}\">&#8617; {Strings.WebParentDir}</a></li>");
        }

        var entries = new DirectoryInfo(localPath).EnumerateFileSystemInfos()
            .OrderByDescending(e => (e.Attributes & FileAttributes.Directory) != 0)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (var entry in entries)
        {
            var isDir = (entry.Attributes & FileAttributes.Directory) != 0;
            var link = basePath + Uri.EscapeDataString(entry.Name) + (isDir ? "/" : "");
            var size = isDir ? "" : HumanSize(((FileInfo)entry).Length);
            rows.Append($"<li><a href=\"{link}\">")
                .Append(isDir ? "&#128193; " : "&#128196; ")
                .Append(WebUtility.HtmlEncode(entry.Name))
                .Append("</a><span class=\"p\">").Append(size).Append("</span></li>");
        }
        return Page(basePath, $"<ul>{rows}</ul>");
    }

    private static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }

    private static string Page(string title, string body) =>
        $$"""
        <!doctype html><html lang="zh"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{WebUtility.HtmlEncode(title)}}</title><style>
        body{font-family:system-ui,"Segoe UI",sans-serif;margin:0;background:#0f1115;color:#e6e6e6}
        header{padding:16px 24px;background:#171a21;border-bottom:1px solid #262a33;font-size:15px}
        ul{list-style:none;margin:0;padding:8px 0}
        li{display:flex;justify-content:space-between;padding:10px 24px;border-bottom:1px solid #1c1f27}
        li:hover{background:#171a21}
        a{color:#7cc4ff;text-decoration:none;word-break:break-all}
        a:hover{text-decoration:underline}
        .p{color:#8b93a1;font-size:12px;margin-left:16px;white-space:nowrap}
        </style></head><body><header>{{WebUtility.HtmlEncode(title)}}</header>{{body}}</body></html>
        """;
}
