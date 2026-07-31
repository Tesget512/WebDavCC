using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace WebDavShare.Core;

public sealed partial class WebDavServer
{
    private static readonly XNamespace Dav = "DAV:";

    private static readonly string[] AllPropNames =
    [
        "resourcetype", "displayname", "getcontentlength", "getlastmodified",
        "creationdate", "getetag", "getcontenttype", "supportedlock", "lockdiscovery",
    ];

    private async Task<int> HandlePropfindAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        var body = await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
        var requested = ParsePropfindBody(body);

        // 拒绝 infinity，避免深层目录递归导致响应爆炸
        var depth = request.Header("Depth") ?? "1";
        if (depth != "0")
        {
            depth = "1";
        }

        if (!TryResolve(request.Path, out var local, out var isVirtualRoot))
        {
            return await SendTextAsync(stream, request, 404, "not found", token);
        }

        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.Append("<D:multistatus xmlns:D=\"DAV:\">");

        if (isVirtualRoot)
        {
            AppendResponse(xml, "/", null, true, "/", requested);
            if (depth == "1")
            {
                foreach (var (name, path) in _config.Shares)
                {
                    AppendResponse(xml, "/" + EncodePath(name) + "/", path, true, name, requested);
                }
            }
        }
        else
        {
            var isDirectory = Directory.Exists(local);
            if (!isDirectory && !File.Exists(local))
            {
                return await SendTextAsync(stream, request, 404, "not found", token);
            }

            var basePath = request.Path.StartsWith('/') ? request.Path : "/" + request.Path;
            if (isDirectory && !basePath.EndsWith('/'))
            {
                basePath += "/";
            }
            var displayName = Path.GetFileName(local!.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = basePath;
            }
            AppendResponse(xml, basePath, local, isDirectory, displayName, requested);

            if (isDirectory && depth == "1")
            {
                foreach (var entry in SafeEnumerate(local))
                {
                    var childIsDir = (entry.Attributes & FileAttributes.Directory) != 0;
                    var href = basePath + EncodePath(entry.Name) + (childIsDir ? "/" : "");
                    AppendResponse(xml, href, entry.FullName, childIsDir, entry.Name, requested);
                }
            }
        }

        xml.Append("</D:multistatus>");
        return await SendXmlAsync(stream, request, 207, xml.ToString(), token);
    }

    private static IEnumerable<FileSystemInfo> SafeEnumerate(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFileSystemInfos()
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>null = allprop；空集 = propname；否则为客户端点名要的属性。</summary>
    private static HashSet<string>? ParsePropfindBody(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }
        try
        {
            var root = XDocument.Parse(Encoding.UTF8.GetString(body)).Root;
            if (root is null || root.Element(Dav + "allprop") is not null)
            {
                return null;
            }
            if (root.Element(Dav + "propname") is not null)
            {
                return [];
            }
            var prop = root.Element(Dav + "prop");
            if (prop is null)
            {
                return null;
            }
            return [.. prop.Elements().Select(e => e.Name.ToString())];
        }
        catch (System.Xml.XmlException)
        {
            return null;   // 畸形 XML 按 allprop 处理，比报错更兼容
        }
    }

    private void AppendResponse(
        StringBuilder xml, string href, string? localPath, bool isDirectory,
        string displayName, HashSet<string>? requested)
    {
        xml.Append("<D:response><D:href>")
           .Append(WebUtility.HtmlEncode(href))
           .Append("</D:href>");

        // propname：只回属性名，不回值
        if (requested is { Count: 0 })
        {
            xml.Append("<D:propstat><D:prop>");
            foreach (var name in AllPropNames)
            {
                xml.Append($"<D:{name}/>");
            }
            xml.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
            return;
        }

        FileSystemInfo? info = null;
        if (localPath is not null)
        {
            try
            {
                info = isDirectory
                    ? new DirectoryInfo(localPath)
                    : new FileInfo(localPath);
                if (!info.Exists)
                {
                    info = null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException)
            {
                info = null;
            }
        }

        bool Want(string name) =>
            requested is null || requested.Contains("{DAV:}" + name);

        var found = new StringBuilder();

        if (Want("resourcetype"))
        {
            found.Append("<D:resourcetype>")
                 .Append(isDirectory ? "<D:collection/>" : "")
                 .Append("</D:resourcetype>");
        }
        if (Want("displayname"))
        {
            found.Append("<D:displayname>")
                 .Append(WebUtility.HtmlEncode(displayName))
                 .Append("</D:displayname>");
        }
        if (Want("getcontentlength"))
        {
            var length = isDirectory ? 0 : (info as FileInfo)?.Length ?? 0;
            found.Append($"<D:getcontentlength>{length}</D:getcontentlength>");
        }
        if (Want("getlastmodified"))
        {
            var time = info?.LastWriteTimeUtc ?? DateTime.UtcNow;
            found.Append("<D:getlastmodified>")
                 .Append(time.ToString("R", CultureInfo.InvariantCulture))
                 .Append("</D:getlastmodified>");
        }
        if (Want("creationdate"))
        {
            var time = info?.CreationTimeUtc ?? DateTime.UtcNow;
            found.Append("<D:creationdate>")
                 .Append(time.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
                 .Append("</D:creationdate>");
        }
        if (Want("getetag"))
        {
            var etag = info is FileInfo file ? MakeETag(file) : "\"collection\"";
            found.Append("<D:getetag>").Append(WebUtility.HtmlEncode(etag)).Append("</D:getetag>");
        }
        if (Want("getcontenttype"))
        {
            found.Append("<D:getcontenttype>")
                 .Append(isDirectory ? "httpd/unix-directory" : MimeTypes.ForPath(displayName))
                 .Append("</D:getcontenttype>");
        }
        if (Want("supportedlock"))
        {
            found.Append("<D:supportedlock><D:lockentry>"
                         + "<D:lockscope><D:exclusive/></D:lockscope>"
                         + "<D:locktype><D:write/></D:locktype></D:lockentry></D:supportedlock>");
        }
        if (Want("lockdiscovery"))
        {
            found.Append("<D:lockdiscovery/>");
        }
        if (Want("quota-available-bytes") || Want("quota-used-bytes"))
        {
            AppendQuota(found, localPath);
        }

        xml.Append("<D:propstat><D:prop>").Append(found)
           .Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>");

        // 客户端点名要但我们没有的属性，按 RFC 4918 回 404
        if (requested is { Count: > 0 })
        {
            var known = new HashSet<string>(AllPropNames.Select(n => "{DAV:}" + n))
            {
                "{DAV:}quota-available-bytes", "{DAV:}quota-used-bytes",
            };
            var missing = requested.Where(name => !known.Contains(name)).ToList();
            if (missing.Count > 0)
            {
                xml.Append("<D:propstat><D:prop>");
                foreach (var name in missing)
                {
                    xml.Append(TagToXml(name));
                }
                xml.Append("</D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat>");
            }
        }
        xml.Append("</D:response>");
    }

    private static void AppendQuota(StringBuilder xml, string? localPath)
    {
        try
        {
            var root = localPath is not null && Directory.Exists(localPath)
                ? Path.GetPathRoot(localPath)
                : Path.GetPathRoot(ConfigStore.DataDir);
            if (root is null)
            {
                return;
            }
            var drive = new DriveInfo(root);
            xml.Append($"<D:quota-available-bytes>{drive.AvailableFreeSpace}</D:quota-available-bytes>");
            xml.Append($"<D:quota-used-bytes>{drive.TotalSize - drive.TotalFreeSpace}</D:quota-used-bytes>");
        }
        catch (Exception e) when (e is IOException or ArgumentException
                                  or UnauthorizedAccessException)
        {
            // 网络盘或权限不足时跳过配额，不影响其他属性
        }
    }

    private static string TagToXml(string qualifiedName)
    {
        if (qualifiedName.StartsWith('{'))
        {
            var close = qualifiedName.IndexOf('}');
            var ns = qualifiedName[1..close];
            var local = qualifiedName[(close + 1)..];
            return ns == "DAV:"
                ? $"<D:{local}/>"
                : $"<X:{local} xmlns:X=\"{WebUtility.HtmlEncode(ns)}\"/>";
        }
        return $"<{qualifiedName}/>";
    }

    /// <summary>逐段编码 URL 路径，保留分隔符。</summary>
    private static string EncodePath(string segment) => Uri.EscapeDataString(segment);

    // ---------------- PROPPATCH ----------------

    private async Task<int> HandleProppatchAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        // 不持久化自定义属性，但必须回 207，否则 Windows 复制文件会报错
        var body = await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
        var names = new List<string>();
        try
        {
            var root = XDocument.Parse(Encoding.UTF8.GetString(body)).Root;
            if (root is not null)
            {
                names.AddRange(root.Descendants(Dav + "prop")
                    .SelectMany(p => p.Elements())
                    .Select(e => e.Name.ToString()));
            }
        }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException)
        {
            // 解析失败也返回成功，客户端只关心状态码
        }

        var props = names.Count > 0
            ? string.Concat(names.Select(n =>
                $"<D:propstat><D:prop>{TagToXml(n)}</D:prop>"
                + "<D:status>HTTP/1.1 200 OK</D:status></D:propstat>"))
            : "<D:propstat><D:prop/><D:status>HTTP/1.1 200 OK</D:status></D:propstat>";

        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                  + "<D:multistatus xmlns:D=\"DAV:\"><D:response>"
                  + $"<D:href>{WebUtility.HtmlEncode(request.Path)}</D:href>"
                  + props + "</D:response></D:multistatus>";
        return await SendXmlAsync(stream, request, 207, xml, token);
    }

    // ---------------- LOCK / UNLOCK ----------------

    private async Task<int> HandleLockAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        var body = await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);

        if (!TryResolve(request.Path, out var local, out _))
        {
            return await SendTextAsync(stream, request, 403, "forbidden", token);
        }

        var timeout = ParseTimeout(request.Header("Timeout"));

        // 无 body + If 头 = 续期
        if (body.Length == 0)
        {
            var ifHeader = request.Header("If") ?? "";
            var start = ifHeader.IndexOf('<');
            var end = ifHeader.IndexOf('>');
            if (start >= 0 && end > start)
            {
                var existingToken = ifHeader[(start + 1)..end];
                if (_locks.Refresh(existingToken, timeout))
                {
                    return await SendXmlAsync(stream, request, 200,
                        LockXml(existingToken, "unknown", timeout, request.Path), token);
                }
            }
            return await SendTextAsync(stream, request, 412, "no such lock", token);
        }

        var owner = "unknown";
        try
        {
            var root = XDocument.Parse(Encoding.UTF8.GetString(body)).Root;
            var ownerElement = root?.Element(Dav + "owner");
            if (ownerElement is not null)
            {
                var text = ownerElement.Value.Trim();
                if (text.Length > 0)
                {
                    owner = text;
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            // owner 只用于展示，解析失败无妨
        }

        // LOCK 一个不存在的资源要创建空文件，这是 Office/资源管理器的保存流程
        var created = false;
        if (local is not null && !File.Exists(local) && !Directory.Exists(local))
        {
            var parent = Path.GetDirectoryName(local);
            if (parent is not null && Directory.Exists(parent))
            {
                await File.WriteAllBytesAsync(local, [], token);
                created = true;
            }
        }

        var lockToken = _locks.Create(local ?? request.Path, owner, timeout);
        return await SendXmlAsync(stream, request, created ? 201 : 200,
            LockXml(lockToken, owner, timeout, request.Path), token,
            $"Lock-Token: <{lockToken}>\r\n");
    }

    private async Task<int> HandleUnlockAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
        var header = (request.Header("Lock-Token") ?? "").Trim().Trim('<', '>');
        _locks.Remove(header);
        return await SendEmptyAsync(stream, request, 204, token);
    }

    private static int ParseTimeout(string? header)
    {
        if (header is not null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                header, @"Second-(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var seconds))
            {
                return Math.Min(seconds, 86400);
            }
        }
        return 3600;
    }

    private static string LockXml(string token, string owner, int timeout, string href) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<D:prop xmlns:D=\"DAV:\"><D:lockdiscovery><D:activelock>"
        + "<D:locktype><D:write/></D:locktype>"
        + "<D:lockscope><D:exclusive/></D:lockscope>"
        + "<D:depth>infinity</D:depth>"
        + $"<D:owner>{WebUtility.HtmlEncode(owner)}</D:owner>"
        + $"<D:timeout>Second-{timeout}</D:timeout>"
        + $"<D:locktoken><D:href>{WebUtility.HtmlEncode(token)}</D:href></D:locktoken>"
        + $"<D:lockroot><D:href>{WebUtility.HtmlEncode(href)}</D:href></D:lockroot>"
        + "</D:activelock></D:lockdiscovery></D:prop>";
}

/// <summary>WebDAV 锁。Windows 重定向器写文件前会先 LOCK。</summary>
internal sealed class LockManager
{
    private readonly Dictionary<string, LockEntry> _locks = [];
    private readonly Lock _gate = new();

    private sealed record LockEntry(string Path, string Owner, DateTime Expires);

    public string Create(string path, string owner, int timeoutSeconds)
    {
        var token = "opaquelocktoken:" + Guid.NewGuid();
        lock (_gate)
        {
            Purge();
            _locks[token] = new LockEntry(path, owner,
                DateTime.UtcNow.AddSeconds(timeoutSeconds));
        }
        return token;
    }

    public bool Refresh(string token, int timeoutSeconds)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(token, out var entry))
            {
                return false;
            }
            _locks[token] = entry with { Expires = DateTime.UtcNow.AddSeconds(timeoutSeconds) };
            return true;
        }
    }

    public bool Remove(string token)
    {
        lock (_gate)
        {
            return _locks.Remove(token);
        }
    }

    private void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _locks.Where(p => p.Value.Expires < now).Select(p => p.Key).ToList())
        {
            _locks.Remove(key);
        }
    }
}
