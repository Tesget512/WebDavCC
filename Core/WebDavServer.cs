using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WebDavShare.Core;

public sealed partial class WebDavServer : IAsyncDisposable
{
    public const int CopyBufferSize = 512 * 1024;   // 视频流吞吐用大块
    private const int MaxRequestBodyBytes = 8 * 1024 * 1024;

    private readonly ServerConfig _config;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly LockManager _locks = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public event Action<string>? Log;

    public ServerConfig Config => _config;
    public bool IsRunning { get; private set; }

    public WebDavServer(ServerConfig config, X509Certificate2 certificate)
    {
        _config = config;
        _certificate = certificate;
    }

    public IReadOnlyList<string> AccessUrls()
    {
        var suffix = _config.Shares.Count == 1 && _config.Shares.ContainsKey("") ? "" : "/";
        return CertificateManager.LocalAddresses()
            .Select(ip => $"https://{ip}:{_config.Port}{suffix}")
            .ToList();
    }

    public void Start()
    {
        // 显式绑定，端口占用时立刻抛 SocketException 而非静默抢占
        _listener = new TcpListener(IPAddress.Any, _config.Port);
        _listener.Server.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start(64);
        IsRunning = true;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        try
        {
            client.NoDelay = true;
            using (client)
            await using (var ssl = new SslStream(client.GetStream(), false))
            {
                try
                {
                    await ssl.AuthenticateAsServerAsync(
                        _certificate, false, SslProtocols.Tls12 | SslProtocols.Tls13, false)
                        .WaitAsync(TimeSpan.FromSeconds(15), token);
                }
                catch (Exception e) when (e is AuthenticationException or IOException
                                          or TimeoutException or OperationCanceledException)
                {
                    // 客户端拒绝自签证书时在此断开，属预期情况
                    return;
                }

                await ServeConnectionAsync(ssl, remote, token);
            }
        }
        catch (Exception e) when (e is IOException or SocketException
                                  or OperationCanceledException or InvalidDataException)
        {
            // 播放器快进/关闭时中断连接很常见，不必刷屏
        }
        catch (Exception e)
        {
            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {remote} connection error: {e.Message}");
        }
    }

    private async Task ServeConnectionAsync(Stream stream, string remote, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var request = await HttpParser.ReadRequestAsync(stream, token);
            if (request is null)
            {
                return;
            }

            var status = await DispatchAsync(stream, request, token);
            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {remote} \"{request.Method} "
                        + $"{request.RawTarget}\" {status}");

            if (!request.KeepAlive)
            {
                return;
            }
        }
    }

    private async Task<int> DispatchAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        // OPTIONS 无需认证，否则部分客户端无法探测服务器能力
        if (request.Method == "OPTIONS")
        {
            await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
            return await SendOptionsAsync(stream, request, token);
        }

        if (!IsAuthorized(request))
        {
            await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
            return await SendUnauthorizedAsync(stream, request, token);
        }

        if (_config.ReadOnly && IsWriteMethod(request.Method))
        {
            await HttpParser.ReadBodyAsync(stream, request, MaxRequestBodyBytes, token);
            return await SendTextAsync(stream, request, 403, "server is read-only", token);
        }

        try
        {
            return request.Method switch
            {
                "GET" or "HEAD" or "POST" => await HandleGetAsync(stream, request, token),
                "PROPFIND" => await HandlePropfindAsync(stream, request, token),
                "PUT" => await HandlePutAsync(stream, request, token),
                "DELETE" => await HandleDeleteAsync(stream, request, token),
                "MKCOL" => await HandleMkcolAsync(stream, request, token),
                "COPY" or "MOVE" => await HandleCopyMoveAsync(stream, request, token),
                "LOCK" => await HandleLockAsync(stream, request, token),
                "UNLOCK" => await HandleUnlockAsync(stream, request, token),
                "PROPPATCH" => await HandleProppatchAsync(stream, request, token),
                _ => await SendTextAsync(stream, request, 405, "method not allowed", token),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return await SendTextAsync(stream, request, 403, "access denied", token);
        }
        catch (DirectoryNotFoundException)
        {
            return await SendTextAsync(stream, request, 404, "not found", token);
        }
        catch (FileNotFoundException)
        {
            return await SendTextAsync(stream, request, 404, "not found", token);
        }
        catch (IOException e)
        {
            return await SendTextAsync(stream, request, 500, e.Message, token);
        }
    }

    private static bool IsWriteMethod(string method) => method is
        "PUT" or "DELETE" or "MKCOL" or "COPY" or "MOVE" or "LOCK" or "UNLOCK" or "PROPPATCH";

    // ---------------- 认证 ----------------

    private bool IsAuthorized(HttpRequest request)
    {
        if (!_config.RequiresAuth)
        {
            return true;
        }
        var header = request.Header("Authorization");
        if (header is null ||
            !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var split = raw.IndexOf(':');
            if (split < 0)
            {
                return false;
            }
            var user = raw[..split];
            var password = raw[(split + 1)..];
            // 恒定时间比较，避免时序侧信道
            return CryptographicOperations.FixedTimeEquals(
                       Encoding.UTF8.GetBytes(user), Encoding.UTF8.GetBytes(_config.User))
                   && CryptographicOperations.FixedTimeEquals(
                       Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(_config.Password));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ---------------- 路径映射 ----------------

    /// <summary>URL 路径 -> 本地路径。null 表示虚拟根；false 表示非法。</summary>
    private bool TryResolve(string urlPath, out string? localPath, out bool isVirtualRoot)
    {
        localPath = null;
        isVirtualRoot = false;

        var decoded = Uri.UnescapeDataString(urlPath);
        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries)
                              .Where(s => s != ".")
                              .ToArray();
        if (segments.Contains(".."))
        {
            return false;
        }

        // 单共享且无挂载名时，URL 根即该共享
        if (_config.Shares.Count == 1 && _config.Shares.ContainsKey(""))
        {
            var root = _config.Shares[""];
            return TryCombine(root, segments, 0, out localPath);
        }

        if (segments.Length == 0)
        {
            isVirtualRoot = true;
            return true;
        }
        if (!_config.Shares.TryGetValue(segments[0], out var shareRoot))
        {
            return false;
        }
        return TryCombine(shareRoot, segments, 1, out localPath);
    }

    private static bool TryCombine(string root, string[] segments, int skip, out string? result)
    {
        result = null;
        var combined = Path.GetFullPath(
            segments.Length > skip
                ? Path.Combine([root, .. segments.Skip(skip)])
                : root);

        // 防目录穿越：结果必须在 root 之内
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!combined.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        result = combined;
        return true;
    }

    // ---------------- 响应输出 ----------------

    private static async Task<int> SendOptionsAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 200 OK\r\n");
        headers.Append("DAV: 1,2\r\n");
        headers.Append("MS-Author-Via: DAV\r\n");
        headers.Append("Allow: OPTIONS, GET, HEAD, POST, PUT, DELETE, PROPFIND, "
                       + "PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK\r\n");
        headers.Append("Accept-Ranges: bytes\r\n");
        headers.Append("Content-Length: 0\r\n");
        headers.Append(ConnectionHeader(request));
        headers.Append("\r\n");
        await WriteAsciiAsync(stream, headers.ToString(), token);
        return 200;
    }

    private async Task<int> SendUnauthorizedAsync(
        Stream stream, HttpRequest request, CancellationToken token)
    {
        var headers = "HTTP/1.1 401 Unauthorized\r\n"
                      + "WWW-Authenticate: Basic realm=\"WebDAV\", charset=\"UTF-8\"\r\n"
                      + "Content-Length: 0\r\n"
                      + ConnectionHeader(request)
                      + "\r\n";
        await WriteAsciiAsync(stream, headers, token);
        return 401;
    }

    private static async Task<int> SendTextAsync(
        Stream stream, HttpRequest request, int status, string body, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var headers = $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n"
                      + "Content-Type: text/plain; charset=utf-8\r\n"
                      + $"Content-Length: {payload.Length}\r\n"
                      + ConnectionHeader(request)
                      + "\r\n";
        await WriteAsciiAsync(stream, headers, token);
        if (request.Method != "HEAD")
        {
            await stream.WriteAsync(payload, token);
        }
        return status;
    }

    private static async Task<int> SendXmlAsync(
        Stream stream, HttpRequest request, int status, string xml,
        CancellationToken token, string? extraHeaders = null)
    {
        var payload = Encoding.UTF8.GetBytes(xml);
        var headers = $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n"
                      + "Content-Type: application/xml; charset=\"utf-8\"\r\n"
                      + $"Content-Length: {payload.Length}\r\n"
                      + (extraHeaders ?? "")
                      + ConnectionHeader(request)
                      + "\r\n";
        await WriteAsciiAsync(stream, headers, token);
        if (request.Method != "HEAD")
        {
            await stream.WriteAsync(payload, token);
        }
        return status;
    }

    private static async Task<int> SendEmptyAsync(
        Stream stream, HttpRequest request, int status, CancellationToken token,
        string? extraHeaders = null)
    {
        var headers = $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n"
                      + "Content-Length: 0\r\n"
                      + (extraHeaders ?? "")
                      + ConnectionHeader(request)
                      + "\r\n";
        await WriteAsciiAsync(stream, headers, token);
        return status;
    }

    private static string ConnectionHeader(HttpRequest request) =>
        request.KeepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n";

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken token) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(text), token).AsTask();

    internal static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        207 => "Multi-Status",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        412 => "Precondition Failed",
        415 => "Unsupported Media Type",
        416 => "Range Not Satisfiable",
        423 => "Locked",
        500 => "Internal Server Error",
        _ => "Unknown",
    };

    public async ValueTask DisposeAsync()
    {
        if (!IsRunning)
        {
            return;
        }
        IsRunning = false;
        await _shutdown.CancelAsync();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // 已关闭
        }
        if (_acceptLoop is not null)
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5))
                             .ContinueWith(_ => { }, TaskScheduler.Default);
        }
        _shutdown.Dispose();
    }
}
